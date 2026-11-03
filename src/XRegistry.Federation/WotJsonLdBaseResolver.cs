// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VDS.RDF.JsonLd;

namespace XRegistry.Federation;

/// <summary>Resolves WoT URI-references with JSON-LD 1.1 base processing and no live context retrieval.</summary>
public static partial class WotJsonLdBaseResolver
{
    private const string ProbePredicatePrefix = "https://xregistry.invalid/.well-known/jsonld-probe/reference/";

    /// <summary>Resolves a URI-reference against its JSON-LD base at an authored JSON Pointer.</summary>
    /// <param name="document">The exact captured WoT document bytes.</param>
    /// <param name="sourcePointer">An RFC 6901 pointer to the object containing the reference occurrence.</param>
    /// <param name="referencePropertyName">The exact authored property name containing the reference.</param>
    /// <param name="reference">The expected reference value at that property.</param>
    /// <param name="documentUri">The absolute URI identifying the captured document.</param>
    /// <param name="capturedContexts">Exact remote context documents already captured for this operation.</param>
    /// <param name="budget">The cumulative read budget shared with the caller.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The resolved target and its document-local classification.</returns>
    /// <exception cref="FederationException">The pointer, document, context set or JSON-LD expansion is invalid or unavailable.</exception>
    public static async ValueTask<WotReferenceUri> ResolveReferenceUriAsync(FederationDocument document,
        string sourcePointer, string referencePropertyName, string reference, Uri documentUri,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sourcePointer);
        ArgumentNullException.ThrowIfNull(referencePropertyName);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        cancellationToken.ThrowIfCancellationRequested();

        var readBudget = budget ?? new();
        var documentBase = FederationJson.AbsoluteUri(document.Base ?? documentUri.AbsoluteUri);
        var root = await ParseDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        var occurrence = ObjectAtPointer(root, sourcePointer, readBudget, cancellationToken);
        if (referencePropertyName.Length == 0 ||
            !occurrence.TryGetValue(referencePropertyName, StringComparison.Ordinal, out var authoredReference) ||
            authoredReference?.Type != JTokenType.String || authoredReference.Value<string>() != reference)
        {
            throw FederationJson.Invalid("The reference value does not match the captured source pointer.");
        }

        FederationJson.UriReference(reference);
        if (occurrence.TryGetValue("@context", StringComparison.Ordinal, out var resetContext) &&
            resetContext?.Type == JTokenType.Null)
        {
            return WotReferenceUriResolver.Resolve(reference, documentBase, documentUri);
        }

        var contextDocuments = CaptureContextDocuments(capturedContexts, readBudget, cancellationToken);

        var loadedContexts = new Dictionary<string, JToken>(StringComparer.Ordinal);
        var baseline = ExpandCapturedDocument(root, documentBase, contextDocuments, loadedContexts,
            readBudget, cancellationToken);
        var scopedTarget = ResolveReferenceValue(root, occurrence, sourcePointer, referencePropertyName,
            reference, documentBase, contextDocuments, loadedContexts, baseline, readBudget, cancellationToken);
        if (scopedTarget is not null)
        {
            return WotReferenceUriResolver.ResolveTarget(scopedTarget, documentUri);
        }
        var marker = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            sourcePointer + "\n" + referencePropertyName + "\n" + reference))).ToLowerInvariant();
        var predicate = ProbePredicatePrefix + marker;
        var suffix = 0;
        while (ContainsProperty(root, predicate, readBudget, cancellationToken) ||
            ContainsProperty(baseline, predicate, readBudget, cancellationToken) ||
            loadedContexts.Values.Any(context => ContainsProperty(context, predicate, readBudget, cancellationToken)))
        {
            suffix++;
            predicate = ProbePredicatePrefix + marker + ":" + suffix.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        occurrence[predicate] = new JObject { ["@id"] = reference };
        readBudget.CheckResultBytes(Encoding.UTF8.GetByteCount(root.ToString(Formatting.None)));
        var expanded = ExpandCapturedDocument(root, documentBase, contextDocuments, loadedContexts,
            readBudget, cancellationToken);
        var probeUris = new HashSet<string>(StringComparer.Ordinal);
        CollectProbeUris(expanded, predicate, probeUris, readBudget, cancellationToken);
        if (probeUris.Count != 1)
        {
            throw FederationJson.Invalid("The JSON-LD reference object has no unique resolved target.");
        }

        var resolvedTarget = FederationJson.AbsoluteUri(probeUris.Single());
        return WotReferenceUriResolver.ResolveTarget(resolvedTarget, documentUri);
    }

    private static async ValueTask<JObject> ParseDocumentAsync(FederationDocument document,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        budget.CheckObjectBytes(document.Length);
        using var stream = document.OpenRead();
        RegistryJson parsed;
        try
        {
            parsed = await RegistryJson.ParseAsync(stream, new RegistryJsonLimits
            {
                MaxBytes = budget.Limits.MaxObjectBytes,
                MaxDepth = budget.Limits.MaxJsonDepth,
                MaxNodes = (int)Math.Min(int.MaxValue, budget.Limits.MaxWork)
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
        FederationJson.RequireObject(parsed.RootElement);
        FederationJson.CountWork(parsed.RootElement, budget, cancellationToken);
        return JObject.Parse(parsed.RootElement.GetRawText());
    }

    private static RemoteDocument LoadContext(Uri uri, Dictionary<string, FederationDocument> contexts,
        Dictionary<string, JToken> loaded, FederationReadBudget budget, Action<FederationException> setFailure,
        CancellationToken cancellationToken, Action<JToken>? transform = null)
    {
        try
        {
            return LoadContextCore(uri, contexts, loaded, budget, transform, cancellationToken);
        }
        catch (FederationException exception)
        {
            setFailure(exception);
            throw;
        }
    }

    private static RemoteDocument LoadContextCore(Uri uri, Dictionary<string, FederationDocument> contexts,
        Dictionary<string, JToken> loaded, FederationReadBudget budget,
        Action<JToken>? transform, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = FederationJson.AbsoluteUri(uri.AbsoluteUri);
        if (!contexts.TryGetValue(normalized.AbsoluteUri, out var document))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "A JSON-LD context was not present in the captured context set.");
        }

        if (!loaded.TryGetValue(normalized.AbsoluteUri, out var context))
        {
            budget.CheckObjectBytes(document.Length);
            budget.ChargeBytes(document.Length);
            budget.ChargeObject();
            using var stream = document.OpenRead();
            try
            {
                var bytes = new byte[(int)document.Length];
                stream.ReadExactly(bytes);
                var json = RegistryJson.Parse(bytes, new RegistryJsonLimits
                {
                    MaxBytes = budget.Limits.MaxObjectBytes,
                    MaxDepth = budget.Limits.MaxJsonDepth,
                    MaxNodes = (int)Math.Min(int.MaxValue, budget.Limits.MaxWork)
                });
                FederationJson.RequireObject(json.RootElement);
                FederationJson.CountWork(json.RootElement, budget, cancellationToken);
                context = JToken.Parse(json.RootElement.GetRawText());
            }
            catch (RegistryException exception)
            {
                throw FederationJson.FromCore(exception);
            }
            loaded.Add(normalized.AbsoluteUri, context);
        }

        var ownedContext = context.DeepClone();
        transform?.Invoke(ownedContext);
        budget.CheckResultBytes(Encoding.UTF8.GetByteCount(ownedContext.ToString(Formatting.None)));
        return new RemoteDocument
        {
            DocumentUrl = FederationJson.AbsoluteUri(document.Base ?? normalized.AbsoluteUri),
            Document = ownedContext
        };
    }

    private static JObject ObjectAtPointer(JToken root, string pointer, FederationReadBudget budget,
        CancellationToken cancellationToken)
    {
        if (pointer.Length != 0 && pointer[0] != '/')
        {
            throw FederationJson.Invalid("A source pointer must be an RFC 6901 JSON Pointer.");
        }

        var current = root;
        foreach (var encoded in pointer.Length == 0 ? [] : pointer[1..].Split('/'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.CheckDepth(pointer.Length == 0 ? 0 : pointer.Count(character => character == '/'));
            budget.ChargeWork();
            var token = DecodePointerToken(encoded);
            if (current is JObject obj && obj.TryGetValue(token, StringComparison.Ordinal, out var property))
            {
                current = property!;
            }
            else if (current is JArray array && IsArrayIndex(token, out var index) && index < array.Count)
            {
                current = array[index]!;
            }
            else
            {
                throw FederationJson.Invalid("The source pointer does not identify a JSON value.");
            }
        }

        return current as JObject ?? throw FederationJson.Invalid("A reference occurrence pointer must identify an object.");
    }

    private static string DecodePointerToken(string token)
    {
        var result = new StringBuilder(token.Length);
        for (var index = 0; index < token.Length; index++)
        {
            if (token[index] != '~') { result.Append(token[index]); continue; }
            if (++index >= token.Length || token[index] is not ('0' or '1'))
            {
                throw FederationJson.Invalid("A source pointer contains an invalid escape.");
            }
            result.Append(token[index] == '0' ? '~' : '/');
        }
        return result.ToString();
    }

    private static bool IsArrayIndex(string value, out int index) =>
        int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out index) &&
        index >= 0 && index.ToString(System.Globalization.CultureInfo.InvariantCulture) == value;

    private static bool ContainsProperty(JToken node, string name, FederationReadBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (node is JObject obj)
        {
            return obj.Property(name, StringComparison.Ordinal) is not null ||
                obj.Properties().Any(property => ContainsProperty(property.Value, name, budget, cancellationToken));
        }
        return node is JArray array && array.Any(item => ContainsProperty(item, name, budget, cancellationToken));
    }

    private static void CollectProbeUris(JToken node, string predicate, HashSet<string> values,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (node is JObject obj)
        {
            if (obj.Property("@value", StringComparison.Ordinal) is not null) { return; }
            if (obj.TryGetValue(predicate, StringComparison.Ordinal, out var probe) && probe is JArray entries)
            {
                foreach (var entry in entries.OfType<JObject>())
                {
                    if (entry.TryGetValue("@id", StringComparison.Ordinal, out var id) &&
                        id.Type == JTokenType.String && id.Value<string>() is { } value)
                    {
                        values.Add(value);
                    }
                }
            }
            foreach (var property in obj.Properties())
            {
                CollectProbeUris(property.Value, predicate, values, budget, cancellationToken);
            }
        }
        else if (node is JArray array)
        {
            foreach (var item in array)
            {
                CollectProbeUris(item, predicate, values, budget, cancellationToken);
            }
        }
    }
}
