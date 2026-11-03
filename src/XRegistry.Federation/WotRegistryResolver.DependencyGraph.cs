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
            var rules = await WotJsonLdBaseResolver.ReadProjectionSelectionRulesAsync(node.Document.Document,
                node.DocumentUri, node.Document.Kind, contexts, readBudget, cancellationToken).ConfigureAwait(false);
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
        cancellationToken.ThrowIfCancellationRequested();
        return new(roots, nodes, edges);
    }
}
