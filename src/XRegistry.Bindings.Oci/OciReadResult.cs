using System.Text.Json;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>A detached native Core document view or verified domain document, with source identity retained separately.</summary>
public sealed class OciReadResult
{
    internal OciReadResult(string target, string selectedXid, NativeRegistryContext context,
        JsonElement value = default, string pointer = "", FederationDocument? document = null,
        JsonElement externalDocument = default)
    {
        Target = target;
        SelectedXid = selectedXid;
        Context = context;
        Value = value.ValueKind == JsonValueKind.Undefined ? default : value.Clone();
        JsonPointer = pointer;
        Document = document;
        ExternalDocument = externalDocument.ValueKind == JsonValueKind.Undefined ? default : externalDocument.Clone();
    }

    /// <summary>The original requested source XID, including an alias identity before one-hop document resolution.</summary>
    public string Target { get; }
    /// <summary>The selected entity or actual resolved Version XID. It is not an OCI digest.</summary>
    public string SelectedXid { get; }
    /// <summary>The source and pinned snapshot context, not a domain-document URI base.</summary>
    public NativeRegistryContext Context { get; }
    /// <summary>The complete standalone metadata document. Navigation pointers are relative to this value, not an enclosing envelope.</summary>
    public JsonElement Value { get; }
    /// <summary>An RFC6901 pointer selecting the entity within Value; empty means its root. Standalone Meta selects /meta.</summary>
    public string JsonPointer { get; }
    /// <summary>Independently owned exact verified bytes, including a present zero-byte document.</summary>
    public FederationDocument? Document { get; }
    /// <summary>An explicitly requested, unfetched external locator descriptor; never a successful byte retrieval.</summary>
    public JsonElement ExternalDocument { get; }
}
