// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Federation;

/// <summary>One closure binding retaining its authored source pointer, operator and href.</summary>
public sealed record WotPublicationClosureBinding(string Source, string SourcePointer, string Operator,
    string Href, string Target);

/// <summary>A structurally validated closure document, not evidence that its captured dependencies are complete.</summary>
public sealed class WotPublicationClosure
{
    /// <summary>The required publication extension's exact URI.</summary>
    public const string ExtensionUri = "http://opcfoundation.org/UA/WoT-Binding/publication-closure/1";

    /// <summary>The selected closure document format.</summary>
    public const string Format = "wot-publication-closure/1";

    private WotPublicationClosure(List<string> entries, List<WotPublicationClosureBinding> bindings)
    {
        Entries = entries.AsReadOnly();
        Bindings = bindings.AsReadOnly();
    }

    /// <summary>Gets exact entry Version XIDs in authored order.</summary>
    public IReadOnlyList<string> Entries { get; }
    /// <summary>Gets bindings in authored order without replacing their XID or href spellings.</summary>
    public IReadOnlyList<WotPublicationClosureBinding> Bindings { get; }

    /// <summary>Parses the closed representation and rejects equivalent duplicate entry or binding identities.</summary>
    /// <remarks>
    /// This checks syntax, not captured Registry membership, target kind, source occurrences,
    /// reachability, binding completeness or whether a pinned view can be reconstructed.
    /// No source is read and no publication extension is registered as supported.
    /// </remarks>
    public static WotPublicationClosure Parse(ReadOnlyMemory<byte> utf8Json, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        var readBudget = budget ?? new();
        var root = FederationJson.Parse(utf8Json, readBudget, cancellationToken);
        FederationJson.Fields(root, "format", "entries", "bindings");
        if (FederationJson.String(root, "format") != Format)
        {
            throw FederationJson.Invalid("Unknown WoT publication closure format.");
        }
        var entryValues = FederationJson.Required(root, "entries");
        var bindingValues = FederationJson.Required(root, "bindings");
        if (entryValues.ValueKind != JsonValueKind.Array || entryValues.GetArrayLength() == 0 ||
            bindingValues.ValueKind != JsonValueKind.Array)
        {
            throw FederationJson.Invalid("A WoT closure requires nonempty entries and a bindings array.");
        }
        var entries = new List<string>();
        var entryKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in entryValues.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (value.ValueKind != JsonValueKind.String)
            {
                throw FederationJson.Invalid("A WoT closure entry must be an exact Version XID string.");
            }
            var xid = value.GetString()!;
            if (!entryKeys.Add(VersionKey(xid)))
            {
                throw FederationJson.Invalid("A WoT closure cannot repeat an entry Version.");
            }
            entries.Add(xid);
        }
        var bindings = new List<WotPublicationClosureBinding>();
        var bindingKeys = new HashSet<(string Source, string Pointer)>();
        foreach (var value in bindingValues.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            FederationJson.Fields(value, "source", "pointer", "operator", "href", "target");
            var source = FederationJson.String(value, "source");
            var sourceKey = VersionKey(source);
            var pointer = FederationJson.String(value, "pointer", allowEmpty: true);
            ValidatePointer(pointer, readBudget, cancellationToken);
            var operation = FederationJson.String(value, "operator");
            if (operation is not ("tm:extends" or "tm:submodel" or "tm:ref" or "td:extends" or
                "td:subthing" or "td:ref" or "type") &&
                operation != WotJsonLdBaseResolver.ProjectionVocabulary + "projects")
            {
                throw FederationJson.Invalid("A WoT closure binding must use a canonical operator.");
            }
            var href = FederationJson.String(value, "href", allowEmpty: true);
            var target = FederationJson.String(value, "target");
            _ = VersionKey(target);
            if (!bindingKeys.Add((sourceKey, pointer)))
            {
                throw FederationJson.Invalid("A WoT closure cannot repeat a source and pointer binding key.");
            }
            bindings.Add(new(source, pointer, operation, href, target));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(entries, bindings);
    }

    private static string VersionKey(string xid)
    {
        var parts = FederationSyntax.Xid(xid);
        if (parts.Length != 6 || xid.Split('/')[5] != "versions")
        {
            throw FederationJson.Invalid("A WoT closure identity must be an exact Version XID.");
        }
        return DocumentTreeFormat.XidPath(parts);
    }

    private static void ValidatePointer(string pointer, FederationReadBudget budget, CancellationToken token)
    {
        if (pointer.Length != 0 && pointer[0] != '/')
        {
            throw FederationJson.Invalid("A closure pointer must be an RFC 6901 JSON Pointer, not a URI fragment.");
        }
        budget.CheckDepth(pointer.Count(character => character == '/'));
        for (var index = 0; index < pointer.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (pointer[index] == '~' && (++index >= pointer.Length || pointer[index] is not ('0' or '1')))
            {
                throw FederationJson.Invalid("A closure pointer contains an invalid tilde escape.");
            }
        }
    }
}
