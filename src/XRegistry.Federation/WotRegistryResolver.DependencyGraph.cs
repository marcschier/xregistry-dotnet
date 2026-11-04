// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;

namespace XRegistry.Federation;

/// <summary>One exact retained entry document and its captured document URI.</summary>
public sealed record WotCapturedGraphEntry(WotResolvedDocument Document, Uri DocumentUri);

/// <summary>One discovered graph node retaining exact bytes and authored occurrence evidence.</summary>
public sealed record WotCapturedDependencyNode(string VersionXid, WotResolvedDocument Document, Uri DocumentUri,
    WotCapturedReferenceInventory Inventory);

/// <summary>One authored external occurrence and its independently selected exact target node.</summary>
public sealed record WotCapturedDependencyEdge(WotCapturedDependencyNode Source,
    WotAuthoredReferenceOccurrence Reference, WotCapturedDependencyNode Target);

/// <summary>A bounded authored dependency graph, not ordinary WoT composition or required-extension acceptance.</summary>
public sealed class WotCapturedDependencyGraph
{
    internal WotCapturedDependencyGraph(List<WotCapturedDependencyNode> entries,
        List<WotCapturedDependencyNode> nodes, List<WotCapturedDependencyEdge> edges)
    {
        Entries = entries.AsReadOnly();
        Nodes = nodes.AsReadOnly();
        Edges = edges.AsReadOnly();
    }

    /// <summary>Gets the distinct caller-selected entry revisions in supplied order.</summary>
    public IReadOnlyList<WotCapturedDependencyNode> Entries { get; }
    /// <summary>Gets exact reachable revisions in first-discovery order.</summary>
    public IReadOnlyList<WotCapturedDependencyNode> Nodes { get; }
    /// <summary>Gets one edge per discovered external occurrence, including repeated targets and cycles.</summary>
    public IReadOnlyList<WotCapturedDependencyEdge> Edges { get; }
}

public static partial class WotRegistryResolver
{
    /// <summary>Traverses authored dependencies using only authorized captured Registry sources and contexts.</summary>
    /// <remarks>
    /// Ordinary cycles reuse retained nodes; every occurrence still has its own edge and requirements.
    /// Projection source pins cover unresolved bytes. This does not resolve nested plans, establish
    /// complete projection admission, discover every syntactically hidden JSON-LD subtree or compose
    /// an ordinary view. No Publication extension is registered as supported by this traversal.
    /// </remarks>
    public static async ValueTask<WotCapturedDependencyGraph> CaptureDependencyGraphAsync(
        IReadOnlyList<WotCapturedGraphEntry> entries,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts, WotCapturedUriIdentityIndex capturedIdentities,
        bool allowPrerelease = false, FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        ArgumentNullException.ThrowIfNull(capturedIdentities);
        cancellationToken.ThrowIfCancellationRequested();
        if (entries.Count == 0) { throw FederationJson.Invalid("A captured dependency graph requires at least one entry."); }
        var readBudget = budget ?? new();
        var contexts = WotJsonLdBaseResolver.SnapshotProjectionContexts(capturedContexts, readBudget, cancellationToken);
        var nodes = new List<WotCapturedDependencyNode>();
        var roots = new List<WotCapturedDependencyNode>();
        var rootKeys = new HashSet<(NativeRegistryContext Context, string Xid)>();
        var edges = new List<WotCapturedDependencyEdge>();
        var known = new Dictionary<(NativeRegistryContext Context, string Xid), WotCapturedDependencyNode>();
        var sourceContexts = new HashSet<NativeRegistryContext>();
        var pending = new Queue<(WotCapturedDependencyNode Node, int Depth)>();
        long resultBytes = 0;

        void ChargeResult(long bytes)
        {
            resultBytes = checked(resultBytes + bytes);
            readBudget.CheckResultBytes(resultBytes);
        }

        async ValueTask<WotCapturedDependencyNode> Add(WotResolvedDocument document, Uri documentUri, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(documentUri);
            if (!documentUri.IsAbsoluteUri)
            {
                throw FederationJson.Invalid("A captured dependency document requires an absolute document URI.");
            }
            _ = FederationJson.AbsoluteUri(documentUri.AbsoluteUri);
            if (!document.Context.IsImmutable)
            {
                throw FederationJson.Invalid("A dependency graph entry must retain an immutable captured revision.");
            }
            var xid = DocumentTreeFormat.XidPath(FederationSyntax.Xid(document.ResourceXid +
                "/versions/" + Uri.EscapeDataString(document.Version.VersionId)));
            var key = (document.Context, xid);
            if (known.TryGetValue(key, out var existing))
            {
                if (existing.Document.Version != document.Version || existing.Document.Kind != document.Kind ||
                    !string.Equals(existing.Document.Document.Base ?? existing.DocumentUri.AbsoluteUri,
                        document.Document.Base ?? documentUri.AbsoluteUri, StringComparison.Ordinal))
                {
                    throw FederationJson.Invalid("One captured Version has contradictory bytes, kind or document-base evidence.");
                }
                await CheckCapturedDocumentUriConsistencyAsync(existing, documentUri, contexts, readBudget,
                    cancellationToken).ConfigureAwait(false);
                return existing;
            }
            readBudget.CheckDepth(depth);
            readBudget.CheckHops(depth);
            if (sourceContexts.Add(document.Context)) { readBudget.ChargeSource(); }
            ChargeResult(document.Document.Length + Encoding.UTF8.GetByteCount(xid) +
                Encoding.UTF8.GetByteCount(documentUri.AbsoluteUri));
            var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document.Document,
                documentUri, document.Kind, contexts, capturedIdentities, readBudget, cancellationToken).ConfigureAwait(false);
            var node = new WotCapturedDependencyNode(xid, document, documentUri, inventory);
            known.Add(key, node);
            nodes.Add(node);
            pending.Enqueue((node, depth));
            return node;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            ArgumentNullException.ThrowIfNull(entry);
            var node = await Add(entry.Document, entry.DocumentUri, 0).ConfigureAwait(false);
            if (!rootKeys.Add((node.Document.Context, node.VersionXid)))
            {
                throw FederationJson.Invalid("A dependency graph cannot repeat an entry revision.");
            }
            roots.Add(node);
        }
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            var node = current.Node;
            var selectorKeys = new HashSet<(string Pointer, string Property)>();
            foreach (var selector in node.Inventory.ProjectionSelectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                selectorKeys.Add((selector.Reference.SourcePointer, selector.Reference.PropertyName));
            }
            var ordinary = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(node.Document.Document,
                node.DocumentUri, contexts, readBudget, cancellationToken).ConfigureAwait(false);
            foreach (var reference in ordinary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (!reference.Target.IsDocumentLocal ||
                    selectorKeys.Contains((reference.SourcePointer, reference.PropertyName))) { continue; }
                _ = await ResolveCapturedLocalReferenceAsync(node.Document, reference, node.DocumentUri,
                    contexts, readBudget, cancellationToken).ConfigureAwait(false);
            }
            var rules = await WotJsonLdBaseResolver.ReadProjectionSelectionRulesAsync(node.Document.Document,
                node.DocumentUri, node.Document.Kind, contexts, readBudget, cancellationToken).ConfigureAwait(false);
            foreach (var rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (rule.Source.Reference.Target.IsDocumentLocal)
                {
                    throw FederationJson.Invalid("A projection plan cannot include itself as a source.");
                }
            }
            var projections = rules.ToDictionary(rule => rule.Source.Reference.SourcePointer,
                rule => rule.Source, StringComparer.Ordinal);
            foreach (var dependency in node.Inventory.Dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (dependency.Target.Source is not CapturedRegistryReadSource)
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation,
                        "Transitive WoT capture requires retained Registry sources rather than live bindings.");
                }
                capturedIdentities.CheckSource(dependency.Target.Source, cancellationToken);
                var reference = dependency.Reference;
                WotResolvedDocument selected;
                if (reference.Operator == WotJsonLdBaseResolver.ProjectionVocabulary + "projects")
                {
                    if (!projections.TryGetValue(reference.SourcePointer, out var source))
                    {
                        throw FederationJson.Invalid("A projection dependency has no source-derived manifest entry.");
                    }
                    selected = await ResolveCapturedProjectionSourceAsync(node.Document, source,
                        node.DocumentUri, contexts, capturedIdentities, allowPrerelease, readBudget,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    selected = (await ResolveCapturedDependencyAsync(node.Document, reference, node.DocumentUri,
                        contexts, capturedIdentities, allowPrerelease, readBudget, cancellationToken)
                        .ConfigureAwait(false)).ContainingDocument;
                }
                var target = await Add(selected, reference.Target.DocumentUri, current.Depth + 1).ConfigureAwait(false);
                ChargeResult(Encoding.UTF8.GetByteCount(reference.SourcePointer) +
                    Encoding.UTF8.GetByteCount(reference.Operator) + Encoding.UTF8.GetByteCount(reference.Href) +
                    Encoding.UTF8.GetByteCount(node.VersionXid) + Encoding.UTF8.GetByteCount(target.VersionXid));
                edges.Add(new(node, reference, target));
            }
        }
        CheckCapturedProjectionCycles(nodes, edges, readBudget, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(roots, nodes, edges);
    }

    private static async ValueTask CheckCapturedDocumentUriConsistencyAsync(WotCapturedDependencyNode existing,
        Uri documentUri, IReadOnlyDictionary<Uri, FederationDocument> contexts, FederationReadBudget budget,
        CancellationToken token)
    {
        if (WotReferenceUriResolver.ResolveTarget(existing.DocumentUri, documentUri).IsDocumentLocal) { return; }
        var document = existing.Document.Document;
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document, existing.DocumentUri,
            contexts, budget, token).ConfigureAwait(false);
        var links = await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(document, existing.DocumentUri,
            existing.Document.Kind, contexts, budget, token).ConfigureAwait(false);
        var sources = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document, existing.DocumentUri,
            existing.Document.Kind, contexts, budget, token).ConfigureAwait(false);
        foreach (var reference in references.Concat(links).Concat(sources.Select(source => source.Reference)))
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            var alternate = WotReferenceUriResolver.ResolveTarget(reference.Target.DocumentUri, documentUri);
            if (reference.Target.IsDocumentLocal != alternate.IsDocumentLocal)
            {
                throw FederationJson.Invalid("One captured Version has document URI aliases with conflicting reference locality.");
            }
        }
    }

    private static void CheckCapturedProjectionCycles(List<WotCapturedDependencyNode> nodes,
        List<WotCapturedDependencyEdge> edges, FederationReadBudget budget, CancellationToken token)
    {
        var incoming = new Dictionary<WotCapturedDependencyNode, int>();
        var outgoing = new Dictionary<WotCapturedDependencyNode, List<WotCapturedDependencyNode>>();
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            incoming.Add(node, 0);
        }
        foreach (var edge in edges)
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (edge.Reference.Operator != WotJsonLdBaseResolver.ProjectionVocabulary + "projects") { continue; }
            incoming[edge.Target]++;
            if (!outgoing.TryGetValue(edge.Source, out var targets))
            {
                targets = [];
                outgoing.Add(edge.Source, targets);
            }
            targets.Add(edge.Target);
        }
        var ready = new Queue<WotCapturedDependencyNode>();
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (incoming[node] == 0) { ready.Enqueue(node); }
        }
        var visited = 0;
        while (ready.TryDequeue(out var node))
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            visited++;
            if (!outgoing.TryGetValue(node, out var targets)) { continue; }
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                budget.ChargeWork();
                if (--incoming[target] == 0) { ready.Enqueue(target); }
            }
        }
        if (visited != nodes.Count)
        {
            throw FederationJson.Invalid("The captured projection-source graph contains a cycle.");
        }
    }
}
