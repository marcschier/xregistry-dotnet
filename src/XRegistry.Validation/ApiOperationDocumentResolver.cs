// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XRegistry.Validation;

/// <summary>
/// Loads an explicitly authorized API dependency from its absolute document URI.
/// A null result means unavailable; an UnauthorizedAccessException means policy denied access.
/// The callback must enforce redirect, credential and origin policy and honor cancellation.
/// </summary>
public delegate ValueTask<ReadOnlyMemory<byte>?> ApiOperationDocumentDependencyResolver(
    string absoluteDocumentUri, CancellationToken cancellationToken);

/// <summary>Describes the API document formats and selectors handled by the optional resolver.</summary>
public sealed class ApiOperationResolverCapabilities
{
    internal ApiOperationResolverCapabilities()
    {
        Formats = Array.AsReadOnly(["OpenAPI/3.0", "OpenAPI/3.1", "Swagger/2.0", "OpenRPC/1.4",
            "AvroProtocol/1.12.0", "Smithy/2.0"]);
        ContentTypes = Array.AsReadOnly(["application/json"]);
        Selectors = Array.AsReadOnly([ApiOperationSelectorKind.Operation, ApiOperationSelectorKind.JsonPointer]);
        SupportsCallerProvidedPathItemReferences = true;
        SupportsCallerProvidedOperationReferences = true;
        MaxReferencedDocuments = 32;
        SupportsApiDocumentValidation = false;
        SupportsOperationResolution = true;
    }

    /// <summary>Gets the exact API format identifiers accepted by the resolver.</summary>
    public ReadOnlyCollection<string> Formats { get; }

    /// <summary>Gets the media types accepted by the resolver.</summary>
    public ReadOnlyCollection<string> ContentTypes { get; }

    /// <summary>Gets the selector forms accepted by the resolver.</summary>
    public ReadOnlyCollection<ApiOperationSelectorKind> Selectors { get; }

    /// <summary>Gets whether operationId selection follows Path Item references supplied by the caller.</summary>
    public bool SupportsCallerProvidedPathItemReferences { get; }

    /// <summary>
    /// Gets whether OpenAPI/Swagger and OpenRPC selection can follow operation references through a caller-supplied resolver.
    /// </summary>
    public bool SupportsCallerProvidedOperationReferences { get; }

    /// <summary>Gets the maximum number of distinct referenced documents loaded for one resolution.</summary>
    public int MaxReferencedDocuments { get; }

    /// <summary>Gets the inclusive per-document byte limit.</summary>
    public int MaxDocumentBytes { get; } = 1_048_576;

    /// <summary>Gets the inclusive operation-wide document byte limit.</summary>
    public long MaxTotalDocumentBytes { get; } = 4_194_304;

    /// <summary>Gets the inclusive reference-chain depth limit.</summary>
    public int MaxReferenceDepth { get; } = 64;

    /// <summary>Gets the inclusive JSON nesting depth limit.</summary>
    public int MaxDocumentDepth { get; } = 64;

    /// <summary>Gets the inclusive operation-wide parsed JSON node limit.</summary>
    public int MaxDocumentNodes { get; } = 100_000;

    /// <summary>Gets the operation deadline, including caller-provided dependency retrieval.</summary>
    public TimeSpan MaxDuration { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets whether full API document validation is available.</summary>
    public bool SupportsApiDocumentValidation { get; }

    /// <summary>Gets whether operation resolution is available.</summary>
    public bool SupportsOperationResolution { get; }
}

/// <summary>An API operation selection or an explicit resolution outcome.</summary>
public sealed class ApiOperationResolutionResult
{
    internal ApiOperationResolutionResult(ApiOperationResolutionStatus status, DocumentDiagnostic? diagnostic = null,
        JsonElement operation = default, string? documentPath = null, Uri? documentUri = null)
    {
        if (status == ApiOperationResolutionStatus.Selected &&
            (operation.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(documentPath)) ||
            status != ApiOperationResolutionStatus.Selected && diagnostic is null)
        {
            throw new ArgumentException("A selected operation or a diagnostic is required.");
        }

        Status = status;
        Diagnostic = diagnostic;
        Operation = operation.ValueKind == JsonValueKind.Undefined ? default : operation.Clone();
        DocumentPath = documentPath;
        DocumentUri = documentUri;
    }

    /// <summary>Gets the resolution outcome.</summary>
    public ApiOperationResolutionStatus Status { get; }

    /// <summary>Gets the selected native JSON operation, method or protocol message object.</summary>
    public JsonElement Operation { get; }

    /// <summary>Gets the document-local JSON Pointer to the selected operation, method or message.</summary>
    public string? DocumentPath { get; }

    /// <summary>Gets the source document URI when the selected operation came from a referenced document.</summary>
    public Uri? DocumentUri { get; }

    /// <summary>Gets the diagnostic for a non-selection outcome.</summary>
    public DocumentDiagnostic? Diagnostic { get; }
}

/// <summary>Reports why an API operation selector did not produce one operation.</summary>
public enum ApiOperationResolutionStatus
{
    /// <summary>Exactly one operation was selected.</summary>
    Selected,

    /// <summary>The document, format declaration, or selected value is invalid.</summary>
    Invalid,

    /// <summary>No matching operation exists in the supplied document.</summary>
    NotFound,

    /// <summary>More than one operation matches the selector.</summary>
    Ambiguous,

    /// <summary>The format, content type, or selector form is not supported.</summary>
    Unsupported,

    /// <summary>An unresolved document reference prevents a conclusive result.</summary>
    Indeterminate,

    /// <summary>Caller policy denied access to a required dependency.</summary>
    Denied,
}

/// <summary>Selects OpenAPI, Swagger, OpenRPC, Avro Protocol and assembled Smithy operations from acquired JSON.</summary>
public static partial class ApiOperationDocumentResolver
{
    private static readonly string[] s_methods =
        ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    [GeneratedRegex(@"\A1\.4\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex OpenRpcVersion();

    [GeneratedRegex(@"\A3\.[01]\.[0-9]+(?:-[^\r\n]+)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex OpenApiVersion();

    /// <summary>Gets the exact optional resolution features available from this implementation.</summary>
    public static ApiOperationResolverCapabilities Capabilities { get; } = new();

    /// <summary>Resolves an operation selector without invoking a dependency resolver.</summary>
    public static ApiOperationResolutionResult Resolve(string format, string contentType,
        ReadOnlyMemory<byte> document, ApiOperationReference reference,
        CancellationToken cancellationToken = default)
    {
        return ResolveAsync(format, contentType, document, reference, cancellationToken: cancellationToken)
            .AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Resolves an operation selector and uses only the reference documents returned by the supplied callback.
    /// No network access occurs unless the callback performs it.
    /// </summary>
    public static async ValueTask<ApiOperationResolutionResult> ResolveAsync(string format, string contentType,
        ReadOnlyMemory<byte> document, ApiOperationReference reference, Uri? documentUri = null,
        ApiOperationDocumentDependencyResolver? resolveReference = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(reference);
        if (documentUri is { IsAbsoluteUri: false })
        {
            throw new ArgumentException("The API document URI must be absolute.", nameof(documentUri));
        }
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = new CancellationTokenSource(Capabilities.MaxDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ResolveCoreAsync(format, contentType, document, reference, documentUri, resolveReference,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return Failure(ApiOperationResolutionStatus.Indeterminate, "limit.duration",
                "The API operation resolution deadline was exceeded.");
        }
    }

    private static async ValueTask<ApiOperationResolutionResult> ResolveCoreAsync(string format, string contentType,
        ReadOnlyMemory<byte> document, ApiOperationReference reference, Uri? documentUri,
        ApiOperationDocumentDependencyResolver? resolveReference, CancellationToken cancellationToken)
    {
        if (!Capabilities.Formats.Contains(format, StringComparer.Ordinal))
        {
            return Failure(ApiOperationResolutionStatus.Unsupported, "format.unsupported",
                $"The API format '{format}' is not supported.");
        }
        if (!string.Equals(contentType.Split(';', 2)[0].Trim(), "application/json",
            StringComparison.OrdinalIgnoreCase))
        {
            return Failure(ApiOperationResolutionStatus.Unsupported, "contenttype.unsupported",
                "The API operation resolver accepts JSON documents only.");
        }
        if (reference.Selector.Kind is not (ApiOperationSelectorKind.Operation or ApiOperationSelectorKind.JsonPointer) ||
            format is "OpenRPC/1.4" or "AvroProtocol/1.12.0" or "Smithy/2.0" &&
            reference.Selector.Kind != ApiOperationSelectorKind.Operation)
        {
            return Failure(ApiOperationResolutionStatus.Unsupported, "selector.unsupported",
                "The API operation selector is not supported for the selected format.");
        }

        var budget = new ValidationContext(new()
        {
            MaxDocumentBytes = Capabilities.MaxDocumentBytes,
            MaxTotalBytes = Capabilities.MaxTotalDocumentBytes,
            MaxDepth = Capabilities.MaxDocumentDepth,
            MaxNodes = Capabilities.MaxDocumentNodes,
            MaxWork = 8_388_608,
        }, cancellationToken);
        JsonDocument parsed;
        try
        {
            parsed = budget.ReadJson(document);
        }
        catch (ValidationFailure failure)
        {
            return ParseFailure(failure, "document.json", "$");
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (format == "Smithy/2.0")
            {
                return ResolveSmithyOperation(root, reference.Selector.Tokens[^1], documentUri, budget);
            }
            var version = ValidateRoot(format, root, cancellationToken);
            if (version is not null)
            {
                return version;
            }

            if (reference.Selector.Kind == ApiOperationSelectorKind.JsonPointer)
            {
                return ResolvePointer(format, root, reference.Selector.Tokens, cancellationToken);
            }
            if (format == "AvroProtocol/1.12.0")
            {
                var selection = ResolveAvroMessage(root, reference.Selector.Tokens[^1], documentUri, cancellationToken);
                if (selection.Status is not (ApiOperationResolutionStatus.Selected or ApiOperationResolutionStatus.NotFound))
                {
                    return selection;
                }
                try
                {
                    new AvroSyntax(format, budget).ReadProtocol(root);
                }
                catch (ValidationFailure failure)
                {
                    return new(failure.Status switch
                    {
                        DocumentValidationStatus.Unsupported => ApiOperationResolutionStatus.Unsupported,
                        DocumentValidationStatus.Indeterminate => ApiOperationResolutionStatus.Indeterminate,
                        _ => ApiOperationResolutionStatus.Invalid
                    }, failure.Diagnostic);
                }
                return selection;
            }
            using var references = new ReferenceContext(parsed, documentUri, resolveReference, budget);
            if (format == "OpenRPC/1.4")
            {
                try
                {
                    return await ResolveOpenRpcMethodAsync(root, reference.Selector.Tokens[^1], references,
                        budget, cancellationToken).ConfigureAwait(false);
                }
                catch (ValidationFailure failure)
                {
                    return new(failure.Status == DocumentValidationStatus.Indeterminate
                        ? ApiOperationResolutionStatus.Indeterminate : ApiOperationResolutionStatus.Invalid,
                        failure.Diagnostic);
                }
            }
            try
            {
                return await ResolveOperationIdAsync(format, root, reference.Selector.Tokens[^1], references,
                    budget, cancellationToken).ConfigureAwait(false);
            }
            catch (ValidationFailure failure)
            {
                return ParseFailure(failure, "document.operation", failure.Diagnostic.Path);
            }
        }
    }

    private static ApiOperationResolutionResult? ValidateRoot(string format, JsonElement root,
        CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.root",
                "An API document must be a JSON object.");
        }
        if (format == "AvroProtocol/1.12.0")
        {
            if (!root.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(protocol.GetString()))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.protocol",
                    "An Avro API document must contain a non-empty protocol declaration.", "$/protocol");
            }
            if (root.TryGetProperty("namespace", out var protocolNamespace) &&
                protocolNamespace.ValueKind != JsonValueKind.String)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.namespace",
                    "An Avro protocol namespace must be a string.", "$/namespace");
            }
            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind != JsonValueKind.Object)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.messages",
                    "Avro protocol messages must be a JSON object.", "$/messages");
            }
            return ValidateAvroProtocolDeclarations(root, cancellationToken);
        }
        if (format == "OpenRPC/1.4")
        {
            if (!root.TryGetProperty("openrpc", out var openRpc) || openRpc.ValueKind != JsonValueKind.String ||
                !OpenRpcVersion().IsMatch(openRpc.GetString()!))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.openrpc_version",
                    "The root openrpc value must match '1.4.*'.", "$/openrpc");
            }
            if (!root.TryGetProperty("methods", out var methods) || methods.ValueKind != JsonValueKind.Array)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.methods",
                    "An OpenRPC document must contain a methods array.", "$/methods");
            }
            return null;
        }

        if (format.StartsWith("OpenAPI/", StringComparison.Ordinal))
        {
            var expectedPrefix = format == "OpenAPI/3.0" ? "3.0." : "3.1.";
            if (!root.TryGetProperty("openapi", out var version) || version.ValueKind != JsonValueKind.String ||
                !version.GetString()!.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
                !OpenApiVersion().IsMatch(version.GetString()!))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.openapi_version",
                    $"The root openapi value does not match '{format}'.", "$/openapi");
            }
        }
        else if (!root.TryGetProperty("swagger", out var swagger) ||
            swagger.ValueKind != JsonValueKind.String || swagger.GetString() != "2.0")
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.swagger_version",
                "The root swagger value must be '2.0'.", "$/swagger");
        }

        if (format == "OpenAPI/3.1" && root.TryGetProperty("webhooks", out var webhookMap) &&
            webhookMap.ValueKind != JsonValueKind.Object)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.webhooks",
                "The webhooks value must be an object.", "$/webhooks");
        }
        if (!root.TryGetProperty("paths", out var paths))
        {
            if (format == "OpenAPI/3.1" &&
                (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Object ||
                 root.TryGetProperty("webhooks", out var webhooks) && webhooks.ValueKind == JsonValueKind.Object))
            {
                return null;
            }
            return Failure(ApiOperationResolutionStatus.Invalid, "document.paths",
                "The API document must contain a paths object.", "$/paths");
        }
        if (paths.ValueKind != JsonValueKind.Object)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.paths",
                "The API document must contain a paths object.", "$/paths");
        }
        return null;
    }

    private static ApiOperationResolutionResult ResolveAvroMessage(JsonElement root, string messageName,
        Uri? documentUri, CancellationToken cancellationToken)
    {
        JsonElement selected = default;
        if (root.TryGetProperty("messages", out var messages))
        {
            foreach (var message in messages.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pointer = "$/messages/" + Escape(message.Name);
                if (message.Value.ValueKind != JsonValueKind.Object)
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.message",
                        "An Avro message declaration must be an object.", pointer);
                }
                if (!message.Value.TryGetProperty("request", out var request) ||
                    request.ValueKind != JsonValueKind.Array)
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.request",
                        "An Avro message must declare a request parameter array.", pointer + "/request");
                }
                var declarationFailure = ValidateAvroRequest(message.Value, request, pointer, cancellationToken);
                if (declarationFailure is not null) { return declarationFailure; }
                if (!message.Value.TryGetProperty("response", out var response) ||
                    response.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.response",
                        "An Avro message must declare a response schema.", pointer + "/response");
                }
                var hasErrors = message.Value.TryGetProperty("errors", out var errors);
                if (hasErrors && errors.ValueKind != JsonValueKind.Array)
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.errors",
                        "An Avro message's declared errors must be an array.", pointer + "/errors");
                }
                if (message.Value.TryGetProperty("one-way", out var oneWay))
                {
                    if (oneWay.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return Failure(ApiOperationResolutionStatus.Invalid, "document.one_way",
                            "An Avro message's one-way flag must be a boolean.", pointer + "/one-way");
                    }
                    var nullResponse = response.ValueKind == JsonValueKind.String && response.GetString() == "null" ||
                        response.ValueKind == JsonValueKind.Object &&
                        response.TryGetProperty("type", out var type) &&
                        type.ValueKind == JsonValueKind.String && type.GetString() == "null";
                    if (oneWay.GetBoolean() && (!nullResponse || hasErrors && errors.GetArrayLength() > 0))
                    {
                        return Failure(ApiOperationResolutionStatus.Invalid, "document.one_way",
                            "A one-way Avro message requires a null response and no declared errors.", pointer + "/one-way");
                    }
                }
                if (string.Equals(message.Name, messageName, StringComparison.Ordinal))
                {
                    selected = message.Value;
                }
            }
        }
        return selected.ValueKind == JsonValueKind.Undefined
            ? Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                $"No message has the exact name '{messageName}'.")
            : Selected(selected, "$/messages/" + Escape(messageName), documentUri);
    }

    private static async ValueTask<ApiOperationResolutionResult> ResolveOpenRpcMethodAsync(JsonElement root,
        string methodName, ReferenceContext references, ValidationContext budget, CancellationToken cancellationToken)
    {
        var matches = new List<OperationLocation>();
        var methodCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var links = new List<OperationLocation>();
        var index = 0;
        foreach (var method in root.GetProperty("methods").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pointer = "$/methods/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var resolved = await ResolveOperationAsync(method, references.Root, pointer, references,
                new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null)
            {
                return resolved.Failure;
            }
            var location = resolved.Operation!;
            if (!location.Operation.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(name.GetString()))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.method_name",
                    "An OpenRPC Method Object must have a non-empty name.", location.Pointer + "/name");
            }
            var contractFailure = await ValidateOpenRpcMethodAsync(location, references, budget,
                links, cancellationToken).ConfigureAwait(false);
            if (contractFailure is not null) { return contractFailure; }
            var nativeName = name.GetString()!;
            methodCounts[nativeName] = Math.Min(2, methodCounts.GetValueOrDefault(nativeName) + 1);
            if (string.Equals(name.GetString(), methodName, StringComparison.Ordinal))
            {
                if (matches.Count < 2) { matches.Add(location); }
            }
        }
        foreach (var link in links)
        {
            budget.Work(path: link.Pointer);
            if (!link.Operation.TryGetProperty("method", out var target)) { continue; }
            var count = methodCounts.GetValueOrDefault(target.GetString()!);
            if (count != 1)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.link_method",
                    "An OpenRPC link method must name exactly one resolved Method Object.",
                    link.Pointer + "/method");
            }
        }
        return matches.Count switch
        {
            0 => Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                $"No method has the exact name '{methodName}'."),
            1 => Selected(matches[0].Operation, matches[0].Pointer, matches[0].DocumentUri),
            _ => Failure(ApiOperationResolutionStatus.Ambiguous, "selection.ambiguous",
                $"More than one method has the exact name '{methodName}'.")
        };
    }

    private static async ValueTask<ApiOperationResolutionResult> ResolveOperationIdAsync(string format, JsonElement root,
        string operationId, ReferenceContext references, ValidationContext budget, CancellationToken cancellationToken)
    {
        var matches = new List<OperationLocation>();
        foreach (var collectionName in format == "OpenAPI/3.1" ? new[] { "paths", "webhooks" } : new[] { "paths" })
        {
            if (!root.TryGetProperty(collectionName, out var paths)) { continue; }
            foreach (var path in paths.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (collectionName == "paths" && path.Name.StartsWith("x-", StringComparison.Ordinal)) { continue; }
                if (collectionName == "paths" && !path.Name.StartsWith('/'))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.path",
                        "A paths entry must start with '/' or be an x- specification extension.",
                        "$/paths/" + Escape(path.Name));
                }
                if (path.Value.ValueKind != JsonValueKind.Object)
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.path_item",
                        "Every operation-map entry must be an object.", "$/" + collectionName + "/" + Escape(path.Name));
                }

                var pathPointer = "$/" + collectionName + "/" + Escape(path.Name);
                var pathItem = await ResolvePathItemAsync(path.Value, references.Root, pathPointer, references,
                    new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                if (pathItem.Failure is not null)
                {
                    return pathItem.Failure;
                }

                foreach (var operation in pathItem.Operations)
                {
                    var failure = await CollectOperationMatchesAsync(operation.Value, operationId,
                        format != "Swagger/2.0", matches, references, budget,
                        new(StringComparer.Ordinal), 0, cancellationToken).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        return failure;
                    }
                }
            }
        }

        return matches.Count switch
        {
            0 => Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                $"No operation has the exact operationId '{operationId}'."),
            1 => Selected(matches[0].Operation, matches[0].Pointer, matches[0].DocumentUri),
            _ => Failure(ApiOperationResolutionStatus.Ambiguous, "selection.ambiguous",
                $"More than one operation has the exact operationId '{operationId}'.")
        };
    }

    private static async ValueTask<PathItemResolution> ResolvePathItemAsync(JsonElement pathItem,
        ReferencedDocument source, string sourcePointer, ReferenceContext references, HashSet<string> referencePath,
        CancellationToken cancellationToken)
    {
        if (pathItem.ValueKind != JsonValueKind.Object)
        {
            return PathItemResolution.Failed(Failure(ApiOperationResolutionStatus.Invalid, "document.path_item",
                "A Path Item reference target must be an object.", sourcePointer));
        }

        var operations = new Dictionary<string, OperationLocation>(StringComparer.Ordinal);
        if (pathItem.TryGetProperty("$ref", out var referenceValue))
        {
            if (referencePath.Count >= Capabilities.MaxReferenceDepth)
            {
                return PathItemResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                    "limit.reference_depth", "The API reference-chain depth limit was exceeded.", sourcePointer));
            }
            if (referenceValue.ValueKind != JsonValueKind.String)
            {
                return PathItemResolution.Failed(Failure(ApiOperationResolutionStatus.Invalid,
                    "dependency.path_item", "A Path Item $ref must be a string.", sourcePointer + "/$ref"));
            }

            var target = await references.ResolveAsync(source, referenceValue.GetString()!, sourcePointer + "/$ref",
                cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null)
            {
                return PathItemResolution.Failed(target.Failure);
            }

            var resolvedTarget = target.Target!;
            var targetIdentity = resolvedTarget.Document.Identity + "#" + resolvedTarget.Pointer;
            if (!referencePath.Add(targetIdentity))
            {
                return PathItemResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                    "dependency.cycle", "A Path Item reference cycle prevents operationId selection.",
                    sourcePointer + "/$ref"));
            }

            var inherited = await ResolvePathItemAsync(resolvedTarget.Value, resolvedTarget.Document,
                resolvedTarget.Pointer, references, referencePath, cancellationToken).ConfigureAwait(false);
            referencePath.Remove(targetIdentity);
            if (inherited.Failure is not null)
            {
                return inherited;
            }

            operations = new(inherited.Operations, StringComparer.Ordinal);
        }

        foreach (var method in s_methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pathItem.TryGetProperty(method, out var operation))
            {
                continue;
            }
            if (operation.ValueKind != JsonValueKind.Object)
            {
                return PathItemResolution.Failed(Failure(ApiOperationResolutionStatus.Invalid,
                    "document.operation", "An HTTP method entry must be an Operation Object.",
                    sourcePointer + "/" + method));
            }
            var resolvedOperation = await ResolveOperationAsync(operation, source, sourcePointer + "/" + method,
                references, new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            if (resolvedOperation.Failure is not null)
            {
                return PathItemResolution.Failed(resolvedOperation.Failure);
            }

            if (operations.ContainsKey(method))
            {
                return PathItemResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                    "dependency.path_item_conflict",
                    "The referenced and containing Path Items both define the same HTTP method.",
                    sourcePointer + "/" + method));
            }
            operations[method] = resolvedOperation.Operation!;
        }

        return PathItemResolution.Succeeded(operations);
    }

    private static async ValueTask<OperationReferenceResolution> ResolveOperationAsync(JsonElement operation,
        ReferencedDocument source, string sourcePointer, ReferenceContext references,
        HashSet<string> referencePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation.ValueKind != JsonValueKind.Object)
        {
            return OperationReferenceResolution.Failed(Failure(ApiOperationResolutionStatus.Invalid,
                "document.operation", "An API operation must be an object.", sourcePointer));
        }
        if (!operation.TryGetProperty("$ref", out var referenceValue))
        {
            return OperationReferenceResolution.Succeeded(new(operation, sourcePointer, source));
        }
        if (referencePath.Count >= Capabilities.MaxReferenceDepth)
        {
            return OperationReferenceResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                "limit.reference_depth", "The API reference-chain depth limit was exceeded.", sourcePointer));
        }
        if (referenceValue.ValueKind != JsonValueKind.String)
        {
            return OperationReferenceResolution.Failed(Failure(ApiOperationResolutionStatus.Invalid,
                "dependency.operation", "An Operation Object $ref must be a string.", sourcePointer + "/$ref"));
        }
        if (operation.EnumerateObject().Any(static property => property.Name is not ("$ref" or "summary" or "description")))
        {
            return OperationReferenceResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                "dependency.operation_siblings",
                "Operation Object reference siblings cannot be merged for operationId selection.",
                sourcePointer));
        }

        var target = await references.ResolveAsync(source, referenceValue.GetString()!, sourcePointer + "/$ref",
            cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null)
        {
            return OperationReferenceResolution.Failed(target.Failure);
        }

        var resolvedTarget = target.Target!;
        if (resolvedTarget.Value.ValueKind != JsonValueKind.Object)
        {
            return OperationReferenceResolution.Failed(Failure(ApiOperationResolutionStatus.Invalid,
                "document.operation", "An Operation Object reference target must be an object.",
                sourcePointer + "/$ref"));
        }

        var targetIdentity = resolvedTarget.Document.Identity + "#" + resolvedTarget.Pointer;
        if (!referencePath.Add(targetIdentity))
        {
            return OperationReferenceResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                "dependency.cycle", "An Operation Object reference cycle prevents operationId selection.",
                sourcePointer + "/$ref"));
        }

        var inherited = await ResolveOperationAsync(resolvedTarget.Value, resolvedTarget.Document,
            resolvedTarget.Pointer, references, referencePath, cancellationToken).ConfigureAwait(false);
        referencePath.Remove(targetIdentity);
        return inherited;
    }

    private static ApiOperationResolutionResult ResolvePointer(string format, JsonElement root, IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var methodStart = 2;
        if (tokens.Count >= 4 && tokens[0] == "components" && format != "Swagger/2.0")
        {
            methodStart = tokens[1] switch
            {
                "pathItems" when format == "OpenAPI/3.1" => 3,
                "callbacks" when tokens.Count >= 5 &&
                    !tokens[3].StartsWith("x-", StringComparison.Ordinal) => 4,
                _ => -1
            };
        }
        else if (tokens.Count < 3 ||
            !(tokens[0] == "paths" && tokens[1].StartsWith('/') ||
              format == "OpenAPI/3.1" && tokens[0] == "webhooks"))
        {
            methodStart = -1;
        }
        if (methodStart < 0 || tokens.Count <= methodStart ||
            (tokens.Count - methodStart - 1) % 4 != 0 ||
            tokens.Count > methodStart + 1 && format == "Swagger/2.0" ||
            !s_methods.Contains(tokens[methodStart], StringComparer.Ordinal))
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "selection.operation_pointer",
                "An operation pointer must select an HTTP method in a supported Path Item or Callback location.");
        }
        for (var index = methodStart + 1; index < tokens.Count; index += 4)
        {
            if (tokens[index] != "callbacks" ||
                tokens[index + 2].StartsWith("x-", StringComparison.Ordinal) ||
                !s_methods.Contains(tokens[index + 3], StringComparer.Ordinal))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "selection.operation_pointer",
                    "A nested operation pointer must select callbacks/{name}/{expression}/{http-method}.");
            }
        }
        if (methodStart == 4 && !IsCallbackExpression(tokens[3]))
        {
            return InvalidCallbackExpression("$/components/callbacks/" + Escape(tokens[2]) + "/" + Escape(tokens[3]));
        }
        for (var index = methodStart + 1; index < tokens.Count; index += 4)
        {
            if (!IsCallbackExpression(tokens[index + 2]))
            {
                return InvalidCallbackExpression("$/" + string.Join("/", tokens.Take(index + 3).Select(Escape)));
            }
        }

        var pathPointer = "$";
        var pathItem = root;
        for (var index = 0; index < methodStart; index++)
        {
            if (pathItem.ValueKind != JsonValueKind.Object || pathItem.TryGetProperty("$ref", out _) ||
                !pathItem.TryGetProperty(tokens[index], out pathItem))
            {
                return Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                    "The selected inline Path Item does not exist; pointers do not traverse references.",
                    pathPointer);
            }
            pathPointer += "/" + Escape(tokens[index]);
        }
        for (var methodIndex = methodStart; ; methodIndex += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pathItem.ValueKind != JsonValueKind.Object ||
                pathItem.TryGetProperty("$ref", out _) ||
                !pathItem.TryGetProperty(tokens[methodIndex], out var operation))
            {
                return Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                    "The selected inline HTTP operation does not exist; pointers do not traverse references.",
                    pathPointer);
            }
            pathPointer += "/" + tokens[methodIndex];
            if (operation.ValueKind != JsonValueKind.Object || operation.TryGetProperty("$ref", out _))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "selection.not_operation",
                    "The selected value is not an inline Operation Object.", pathPointer);
            }
            if (methodIndex == tokens.Count - 1) { return Selected(operation, pathPointer); }
            if (!operation.TryGetProperty("callbacks", out var callbacks) ||
                callbacks.ValueKind != JsonValueKind.Object ||
                !callbacks.TryGetProperty(tokens[methodIndex + 2], out var callback) ||
                callback.ValueKind != JsonValueKind.Object ||
                callback.TryGetProperty("$ref", out _) ||
                !callback.TryGetProperty(tokens[methodIndex + 3], out pathItem))
            {
                return Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                    "The selected inline callback does not exist; pointers do not traverse references.",
                    pathPointer + "/callbacks");
            }
            pathPointer += "/callbacks/" + Escape(tokens[methodIndex + 2]) + "/" + Escape(tokens[methodIndex + 3]);
        }
    }

    private static ApiOperationResolutionResult Selected(JsonElement operation, string pointer, Uri? documentUri = null) =>
        new(ApiOperationResolutionStatus.Selected, operation: operation, documentPath: pointer, documentUri: documentUri);

    private static ApiOperationResolutionResult Failure(ApiOperationResolutionStatus status, string code,
        string detail, string path = "$/selector") =>
        new(status, new(path, code, detail));

    private static ApiOperationResolutionResult ParseFailure(ValidationFailure failure, string code, string path) =>
        Failure(failure.Status == DocumentValidationStatus.Indeterminate
            ? ApiOperationResolutionStatus.Indeterminate : ApiOperationResolutionStatus.Invalid,
            failure.Diagnostic.Code.StartsWith("limit.", StringComparison.Ordinal)
                ? failure.Diagnostic.Code : code,
            failure.Diagnostic.Detail, path);

    private static string Escape(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private sealed record OperationLocation(JsonElement Operation, string Pointer, ReferencedDocument Source)
    {
        internal Uri? DocumentUri => Source.IsRoot ? null : Source.Uri;
    }

    private sealed record PathItemResolution(IReadOnlyDictionary<string, OperationLocation> Operations,
        ApiOperationResolutionResult? Failure)
    {
        internal static PathItemResolution Succeeded(IReadOnlyDictionary<string, OperationLocation> operations) =>
            new(operations, null);

        internal static PathItemResolution Failed(ApiOperationResolutionResult failure) =>
            new(new Dictionary<string, OperationLocation>(), failure);
    }

    private sealed record OperationReferenceResolution(OperationLocation? Operation,
        ApiOperationResolutionResult? Failure)
    {
        internal static OperationReferenceResolution Succeeded(OperationLocation operation) =>
            new(operation, null);

        internal static OperationReferenceResolution Failed(ApiOperationResolutionResult failure) =>
            new(null, failure);
    }

    private sealed class ReferencedDocument(JsonDocument json, Uri? uri, string identity, bool isRoot) : IDisposable
    {
        public JsonDocument Json { get; } = json;
        public Uri? Uri { get; } = uri;
        public string Identity { get; } = identity;
        public bool IsRoot { get; } = isRoot;
        public JsonElement Root => Json.RootElement;

        public void Dispose() => Json.Dispose();
    }

    private sealed record ReferenceTarget(ReferencedDocument Document, JsonElement Value, string Pointer);

    private sealed record ReferenceTargetResolution(ReferenceTarget? Target, ApiOperationResolutionResult? Failure)
    {
        internal static ReferenceTargetResolution Succeeded(ReferenceTarget target) => new(target, null);

        internal static ReferenceTargetResolution Failed(ApiOperationResolutionResult failure) =>
            new(null, failure);
    }

    private sealed class ReferenceContext : IDisposable
    {
        private readonly ApiOperationDocumentDependencyResolver? _resolver;
        private readonly Dictionary<string, ReferencedDocument> _documents = new(StringComparer.Ordinal);
        private readonly ValidationContext _budget;
        private int _externalDocumentCount;

        internal ReferenceContext(JsonDocument root, Uri? rootUri,
            ApiOperationDocumentDependencyResolver? resolver, ValidationContext budget)
        {
            var uri = WithoutFragment(rootUri);
            Root = new(root, uri, uri?.AbsoluteUri ?? "$root", true);
            if (uri is not null)
            {
                _documents.Add(uri.AbsoluteUri, Root);
            }
            _resolver = resolver;
            _budget = budget;
        }

        internal ReferencedDocument Root { get; }

        internal async ValueTask<ReferenceTargetResolution> ResolveAsync(ReferencedDocument source,
            string reference, string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri resolved;
            try
            {
                var fragmentAt = reference.IndexOf('#');
                if (fragmentAt >= 0)
                {
                    _ = SchemaJson.DecodeFragment(reference[(fragmentAt + 1)..], path, "dependency.pointer", _budget);
                }
                if (reference.StartsWith('#'))
                {
                    if (source.Uri is null)
                    {
                        return ReferenceTargetResolution.Succeeded(CreateTarget(source, reference));
                    }
                    resolved = new Uri(source.Uri, reference);
                }
                else if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute))
                {
                    resolved = absolute;
                }
                else if (source.Uri is null)
                {
                    return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                        "dependency.base_uri", "A relative API document reference requires an absolute document URI.",
                        path));
                }
                else if (Uri.TryCreate(source.Uri, reference, out var relative))
                {
                    resolved = relative;
                }
                else
                {
                    return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Invalid,
                        "dependency.uri", "The API document reference is not a valid URI reference.", path));
                }
            }
            catch (UriFormatException exception)
            {
                return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Invalid,
                    "dependency.uri", $"The API document reference URI is invalid: {exception.Message}", path));
            }
            catch (FormatException exception)
            {
                return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Invalid,
                    "dependency.pointer", exception.Message, path));
            }
            catch (ValidationFailure failure)
            {
                return ReferenceTargetResolution.Failed(ParseFailure(failure, "dependency.pointer", path));
            }

            var documentUri = WithoutFragment(resolved)!;
            if (!_documents.TryGetValue(documentUri.AbsoluteUri, out var document))
            {
                if (_resolver is null)
                {
                    return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                        "dependency.unresolved", $"No caller-supplied resolver can load '{documentUri}'.", path));
                }
                if (_externalDocumentCount >= Capabilities.MaxReferencedDocuments)
                {
                    return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                        "limit.references", "The API reference document limit was exceeded.", path));
                }

                ReadOnlyMemory<byte>? bytes;
                try
                {
                    bytes = await _resolver(documentUri.AbsoluteUri, cancellationToken).AsTask()
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException exception)
                {
                    return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Denied,
                        "dependency.denied", $"Caller policy denied '{documentUri}': {exception.Message}", path));
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes is null)
                {
                    return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate,
                        "dependency.unresolved", $"The caller-supplied resolver did not return '{documentUri}'.", path));
                }
                JsonDocument parsed;
                try
                {
                    parsed = _budget.ReadJson(bytes.Value);
                }
                catch (ValidationFailure failure)
                {
                    return ReferenceTargetResolution.Failed(ParseFailure(failure, "dependency.json", path));
                }

                document = new(parsed, documentUri, documentUri.AbsoluteUri, false);
                _documents.Add(documentUri.AbsoluteUri, document);
                _externalDocumentCount++;
            }

            try
            {
                return ReferenceTargetResolution.Succeeded(CreateTarget(document, resolved.Fragment));
            }
            catch (ValidationFailure failure)
            {
                return ReferenceTargetResolution.Failed(ParseFailure(failure, "dependency.pointer", path));
            }
        }

        private ReferenceTarget CreateTarget(ReferencedDocument document, string reference)
        {
            var (value, pointer) = SchemaJson.Pointer(document.Root,
                reference.StartsWith('#') ? reference : "#" + reference, "$/selector", _budget);
            return new(document, value, pointer);
        }

        private static Uri? WithoutFragment(Uri? uri)
        {
            if (uri is null || string.IsNullOrEmpty(uri.Fragment))
            {
                return uri;
            }
            var builder = new UriBuilder(uri) { Fragment = "" };
            return builder.Uri;
        }

        public void Dispose()
        {
            foreach (var document in _documents.Values)
            {
                if (!ReferenceEquals(document, Root))
                {
                    document.Dispose();
                }
            }
        }
    }
}
