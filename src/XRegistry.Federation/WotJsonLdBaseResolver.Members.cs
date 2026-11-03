// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VDS.RDF.JsonLd;
using VDS.RDF.JsonLd.Syntax;

namespace XRegistry.Federation;

/// <summary>A reference candidate discovered in original WoT bytes, before target-kind classification.</summary>
public sealed record WotAuthoredReferenceOccurrence(string SourcePointer, string PropertyName, string Operator,
    string Href, WotReferenceUri Target);

public static partial class WotJsonLdBaseResolver
{
    private const string TmReferenceIri = "https://www.w3.org/2019/wot/tm#ref";
    private const string TdReferenceIri = "https://www.w3.org/2019/wot/td#ref";

    /// <summary>Discovers ordinary tm:ref and td:ref members under captured effective JSON-LD contexts.</summary>
    /// <remarks>
    /// Results retain authored object pointers, member names and hrefs, including local references.
    /// This does not classify Schema targets, select Versions, discover links or interpret projection plans.
    /// Target classification must precede checking whether several members form an ambiguous occurrence.
    /// </remarks>
    public static async ValueTask<IReadOnlyList<WotAuthoredReferenceOccurrence>> DiscoverOrdinaryReferencesAsync(
        FederationDocument document, Uri documentUri,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        var readBudget = budget ?? new();
        var contextDocuments = CaptureContextDocuments(capturedContexts, readBudget, cancellationToken);
        var contexts = ContextSnapshot(contextDocuments);
        var root = await ParseDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        var documentBase = FederationJson.AbsoluteUri(document.Base ?? documentUri.AbsoluteUri);
        ExpandCapturedDocument(root, documentBase, contextDocuments, new(), readBudget, cancellationToken);
        var occurrences = new List<WotAuthoredReferenceOccurrence>();
        var candidates = new List<(string Pointer, JProperty Property)>();
        CollectAuthoredMembers(root, "", candidates, readBudget, cancellationToken);
        foreach (var (pointer, property) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            var iri = await ExpandMemberIriAsync(document, pointer, property.Name, documentUri,
                contexts, readBudget, cancellationToken).ConfigureAwait(false);
            var operation = iri switch
            {
                TmReferenceIri => "tm:ref",
                TdReferenceIri => "td:ref",
                _ => null
            };
            if (operation is null) { continue; }
            if (property.Value.Type != JTokenType.String)
            {
                throw FederationJson.Invalid("An ordinary WoT reference member must contain an authored string.");
            }
            var href = property.Value.Value<string>()!;
            var target = await ResolveReferenceUriAsync(document, pointer, property.Name, href, documentUri,
                contexts, readBudget, cancellationToken).ConfigureAwait(false);
            occurrences.Add(new(pointer, property.Name, operation, href, target));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return occurrences.AsReadOnly();
    }

    /// <summary>Expands one authored member name without replacing its source value or interpreting a literal prefix.</summary>
    /// <returns>The expanded member IRI, or null when JSON-LD does not expose that member as a semantic property.</returns>
    public static async ValueTask<string?> ExpandMemberIriAsync(FederationDocument document,
        string sourcePointer, string propertyName, Uri documentUri,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sourcePointer);
        ArgumentNullException.ThrowIfNull(propertyName);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        var readBudget = budget ?? new();
        var root = await ParseDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        var occurrence = ObjectAtPointer(root, sourcePointer, readBudget, cancellationToken);
        if (propertyName.StartsWith('@') || occurrence.Property(propertyName, StringComparison.Ordinal) is null)
        {
            throw FederationJson.Invalid("Member expansion requires an existing non-keyword authored member.");
        }
        var documentBase = FederationJson.AbsoluteUri(document.Base ?? documentUri.AbsoluteUri);
        var contexts = CaptureContextDocuments(capturedContexts, readBudget, cancellationToken);
        var loaded = new Dictionary<string, JToken>(StringComparer.Ordinal);
        var baseline = ExpandCapturedDocument(root, documentBase, contexts, loaded, readBudget, cancellationToken);
        var marker = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            sourcePointer + "\n" + propertyName))).ToLowerInvariant();
        var predicate = "https://xregistry.invalid/.well-known/jsonld-probe/member/" + marker;
        while (ContainsProperty(root, predicate, readBudget, cancellationToken) ||
            ContainsProperty(baseline, predicate, readBudget, cancellationToken) ||
            loaded.Values.Any(context => ContainsProperty(context, predicate, readBudget, cancellationToken)))
        {
            predicate += ":probe";
        }

        // @vocab coercion expands the original member name in the object's active context.
        // Baseline expansion prevents an authored alias from impersonating the synthetic property.
        AddMemberProbeContexts(root, predicate, readBudget, cancellationToken);
        if (root.Property("@context", StringComparison.Ordinal) is null)
        {
            root["@context"] = MemberProbeDefinition(predicate);
        }
        occurrence[predicate] = propertyName;
        readBudget.CheckResultBytes(Encoding.UTF8.GetByteCount(root.ToString(Formatting.None)));
        var expanded = ExpandCapturedDocument(root, documentBase, contexts, loaded, readBudget, cancellationToken,
            context => AddMemberProbeContexts(context, predicate, readBudget, cancellationToken));
        var iris = new HashSet<string>(StringComparer.Ordinal);
        CollectMemberIris(expanded, predicate, iris, readBudget, cancellationToken);
        return iris.Count switch
        {
            0 => null,
            1 => iris.Single(),
            _ => throw FederationJson.Invalid("One authored member expanded to competing semantic properties.")
        };
    }

    private static Dictionary<string, FederationDocument> CaptureContextDocuments(
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts, FederationReadBudget readBudget,
        CancellationToken cancellationToken)
    {
        var contexts = new Dictionary<string, FederationDocument>(StringComparer.Ordinal);
        foreach (var (uri, context) in capturedContexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(context);
            if (!contexts.TryAdd(FederationJson.AbsoluteUri(uri.AbsoluteUri).AbsoluteUri, context))
            {
                throw FederationJson.Invalid("The captured JSON-LD context set contains duplicate URI identities.");
            }
        }
        return contexts;
    }

    private static Dictionary<Uri, FederationDocument> ContextSnapshot(
        Dictionary<string, FederationDocument> contexts) =>
        contexts.ToDictionary(pair => new Uri(pair.Key), pair => pair.Value,
            EqualityComparer<Uri>.Create(
                (left, right) => StringComparer.Ordinal.Equals(left?.AbsoluteUri, right?.AbsoluteUri),
                uri => StringComparer.Ordinal.GetHashCode(uri.AbsoluteUri)));

    private static JArray ExpandCapturedDocument(JObject root, Uri documentBase,
        Dictionary<string, FederationDocument> contexts, Dictionary<string, JToken> loaded,
        FederationReadBudget readBudget, CancellationToken cancellationToken, Action<JToken>? transform = null)
    {
        FederationException? contextFailure = null;
        var options = new JsonLdProcessorOptions
        {
            Base = documentBase,
            ProcessingMode = JsonLdProcessingMode.JsonLd11,
            RemoteContextLimit = (int)Math.Min(readBudget.Limits.MaxDepth, readBudget.Limits.MaxObjects),
            DocumentLoader = (uri, _) => LoadContext(uri, contexts, loaded, readBudget,
                failure => contextFailure = failure, cancellationToken, transform)
        };
        JArray expanded;
        try
        {
            var warnings = new List<JsonLdProcessorWarning>();
            expanded = JsonLdProcessor.Expand(root, options, warnings);
            if (warnings.Count != 0)
            {
                throw FederationJson.Invalid("The captured member produced incomplete JSON-LD expansion.");
            }
        }
        catch (JsonLdProcessorException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contextFailure is not null) { throw contextFailure; }
            if (exception.ErrorCode == JsonLdErrorCode.ContextOverflow)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded,
                    "Captured JSON-LD expansion exhausted the remote-context traversal limit.",
                    innerException: exception);
            }
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The captured member or its contexts could not be expanded.", innerException: exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        readBudget.CheckResultBytes(Encoding.UTF8.GetByteCount(expanded.ToString(Formatting.None)));
        return expanded;
    }

    private static Uri? ResolveReferenceValue(JObject root, JObject occurrence, string sourcePointer,
        string propertyName, string reference, Uri documentBase,
        Dictionary<string, FederationDocument> contexts, Dictionary<string, JToken> loaded,
        JArray baseline, FederationReadBudget budget, CancellationToken cancellationToken)
    {
        var marker = "xregistry-reference-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(sourcePointer + "\n" + propertyName + "\n" + reference))).ToLowerInvariant();
        var occupied = new List<string>();
        CollectMarkedIdObjects(baseline, "", marker, occupied, budget, cancellationToken);
        while (occupied.Count != 0)
        {
            marker += "-probe";
            occupied.Clear();
            CollectMarkedIdObjects(baseline, "", marker, occupied, budget, cancellationToken);
        }
        var fragment = reference.IndexOf('#', StringComparison.Ordinal);
        var markedReference = (fragment < 0 ? reference : reference[..fragment]) + "#" + marker;
        var authored = occurrence[propertyName]!;
        // A bare @id object keeps the original term's property-scoped context and the
        // JSON-LD exception to non-propagating-context reversion for scalar identifiers.
        occurrence[propertyName] = new JObject { ["@id"] = markedReference };
        var marked = ExpandCapturedDocument(root, documentBase, contexts, loaded, budget, cancellationToken);
        var paths = new List<string>();
        CollectMarkedIdObjects(marked, "", marker, paths, budget, cancellationToken);
        if (paths.Count == 0)
        {
            occurrence[propertyName] = authored;
            return null;
        }
        if (paths.Count != 1)
        {
            throw FederationJson.Invalid("The authored reference value has competing expanded target occurrences.");
        }
        occurrence[propertyName] = new JObject { ["@id"] = reference };
        var resolved = ExpandCapturedDocument(root, documentBase, contexts, loaded, budget, cancellationToken);
        var resolvedObject = ObjectAtPointer(resolved, paths[0], budget, cancellationToken);
        if (resolvedObject["@id"] is not JValue { Type: JTokenType.String } target)
        {
            throw FederationJson.Invalid("The authored reference did not retain its expanded identifier location.");
        }
        var markedObject = ObjectAtPointer(marked, paths[0], budget, cancellationToken);
        markedObject["@id"] = target.DeepClone();
        CollectMarkedIdObjects(resolved, "", marker, [], budget, cancellationToken);
        if (!JToken.DeepEquals(marked, resolved))
        {
            throw FederationJson.Invalid("The authored reference changed its expanded structure during URI resolution.");
        }
        occurrence[propertyName] = authored;
        return FederationJson.AbsoluteUri(target.Value<string>()!);
    }

    private static void CollectMarkedIdObjects(JToken token, string pointer, string marker, List<string> paths,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (token is JObject obj)
        {
            if (obj.Property("@value", StringComparison.Ordinal) is not null) { return; }
            if (obj["@id"] is JValue { Type: JTokenType.String } id &&
                id.Value<string>()!.EndsWith("#" + marker, StringComparison.Ordinal))
            {
                paths.Add(pointer);
            }
            foreach (var property in obj.Properties())
            {
                CollectMarkedIdObjects(property.Value, pointer + "/" +
                    property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal),
                    marker, paths, budget, cancellationToken);
            }
        }
        else if (token is JArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                CollectMarkedIdObjects(array[index], pointer + "/" + index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture), marker, paths, budget, cancellationToken);
            }
        }
    }

    private static JObject MemberProbeDefinition(string predicate) => new()
    {
        [predicate] = new JObject
        {
            ["@id"] = predicate, ["@type"] = "@vocab", ["@protected"] = false
        }
    };

    private static void AddMemberProbeContexts(JToken token, string predicate, FederationReadBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties().ToArray())
            {
                if (property.Name == "@context")
                {
                    property.Value = AugmentMemberContext(property.Value, predicate, budget, cancellationToken);
                }
                else
                {
                    AddMemberProbeContexts(property.Value, predicate, budget, cancellationToken);
                }
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array.ToArray())
            {
                AddMemberProbeContexts(item, predicate, budget, cancellationToken);
            }
        }
    }

    private static JToken AugmentMemberContext(JToken context, string predicate, FederationReadBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (context is JObject obj)
        {
            AddMemberProbeContexts(obj, predicate, budget, cancellationToken);
            if (obj.Property(predicate, StringComparison.Ordinal) is not null)
            {
                throw FederationJson.Invalid("A captured context conflicts with a member-expansion probe.");
            }
            obj.Add(predicate, MemberProbeDefinition(predicate)[predicate]!.DeepClone());
        }
        else if (context is JArray array)
        {
            var augmented = new JArray();
            foreach (var item in array)
            {
                var value = AugmentMemberContext(item.DeepClone(), predicate, budget, cancellationToken);
                if (item.Type == JTokenType.Null && value is JArray reset)
                {
                    foreach (var entry in reset) { augmented.Add(entry.DeepClone()); }
                }
                else { augmented.Add(value); }
            }
            return augmented;
        }
        else if (context.Type == JTokenType.Null)
        {
            return new JArray(JValue.CreateNull(), MemberProbeDefinition(predicate));
        }
        return context;
    }

    private static void CollectMemberIris(JToken token, string predicate, HashSet<string> iris,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (token is JObject obj)
        {
            if (obj.Property("@value", StringComparison.Ordinal) is not null) { return; }
            if (obj.TryGetValue(predicate, StringComparison.Ordinal, out var values) && values is JArray entries)
            {
                foreach (var entry in entries.OfType<JObject>())
                {
                    if (entry["@id"] is JValue { Type: JTokenType.String } iri) { iris.Add(iri.Value<string>()!); }
                }
            }
            foreach (var property in obj.Properties())
            {
                CollectMemberIris(property.Value, predicate, iris, budget, cancellationToken);
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array) { CollectMemberIris(item, predicate, iris, budget, cancellationToken); }
        }
    }

    private static void CollectAuthoredMembers(JToken token, string pointer,
        List<(string Pointer, JProperty Property)> members, FederationReadBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                if (property.Name == "@context") { continue; }
                if (!property.Name.StartsWith('@')) { members.Add((pointer, property)); }
                CollectAuthoredMembers(property.Value, pointer + "/" +
                    property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal),
                    members, budget, cancellationToken);
            }
        }
        else if (token is JArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                CollectAuthoredMembers(array[index], pointer + "/" + index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture), members, budget, cancellationToken);
            }
        }
    }
}
