using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

public sealed partial class OciSnapshot
{
    async ValueTask<FederationReadResult> IFederationReadSource.ReadAsync(FederationReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Enter(cancellationToken);
        try
        {
            return await ReadFederationAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref reading, 0); }
    }

    private async ValueTask<FederationReadResult> ReadFederationAsync(FederationReadRequest request, CancellationToken cancellationToken)
    {
        var result = await ReadCoreAsync(request, returnExternalDescriptor: true, cancellationToken).ConfigureAwait(false);
        if (result.Document is not null) { return FederationReadResult.FromDocument(result.SelectedXid, Context, result.Document); }
        if (result.ExternalDocument.ValueKind != JsonValueKind.Undefined)
        {
            return FederationReadResult.FromExternalDocument(result.SelectedXid, Context, result.ExternalDocument);
        }
        if (request.Operation is FederationOperation.Model or FederationOperation.Capabilities)
        {
            return FederationReadResult.FromMetadata(result.SelectedXid, Context, result.Value);
        }
        JsonObject envelope;
        if (request.Operation == FederationOperation.Collection && request.Selector is null)
        {
            var kind = OciFormat.Xid(request.Target, true).Length switch { 1 => "group", 3 => "resource", _ => "version" };
            var entities = new JsonObject();
            foreach (var entity in result.Value.EnumerateObject())
            {
                entities[entity.Name] = Rebase(entity.Value, kind, "/entities/" + OciJson.PointerToken(entity.Name));
            }
            envelope = new() { ["kind"] = "collection", ["xid"] = result.SelectedXid, ["complete"] = true, ["entities"] = entities };
        }
        else if (result.JsonPointer == "/meta")
        {
            var resource = Rebase(result.Value, "resource", "/related/resource");
            var meta = resource["meta"]!.DeepClone().AsObject();
            meta["self"] = "#/entity";
            envelope = new()
            {
                ["kind"] = "meta",
                ["entity"] = meta,
                ["related"] = new JsonObject { ["resource"] = resource },
            };
        }
        else
        {
            var kind = OciFormat.Xid(result.SelectedXid).Length switch { 0 => "registry", 2 => "group", 4 => "resource", _ => "version" };
            envelope = new() { ["kind"] = kind, ["entity"] = Rebase(result.Value, kind, "/entity") };
        }
        return FederationReadResult.FromMetadata(result.SelectedXid, Context, OciEncoding.Result(envelope, session.Budget, cancellationToken));
    }

    private JsonObject Rebase(JsonElement entity, string kind, string pointer)
    {
        var output = OciJson.Copy(entity)!.AsObject();
        output["self"] = "#" + pointer;
        if (kind == "version") { return output; }
        var alias = false;
        if (kind == "resource")
        {
            var meta = output["meta"]!.AsObject();
            alias = meta.ContainsKey("xref");
            meta["self"] = "#" + pointer + "/meta";
            output["metaurl"] = "#" + pointer + "/meta";
            if (!alias)
            {
                meta["defaultversionurl"] = "#" + pointer + "/versions/" + OciJson.PointerToken(meta["defaultversionid"]!.GetValue<string>());
            }
        }
        foreach (var collection in OciRecords.Collections(Model, kind, entity.GetProperty("xid").GetString()!, alias))
        {
            var name = OciFormat.Xid(collection, true)[^1];
            var map = new JsonObject();
            var childKind = kind == "registry" ? "group" : kind == "group" ? "resource" : "version";
            foreach (var child in entity.GetProperty(name).EnumerateObject())
            {
                map[child.Name] = Rebase(child.Value, childKind, pointer + "/" + OciJson.PointerToken(name) + "/" + OciJson.PointerToken(child.Name));
            }
            output[name] = map;
            output[name + "url"] = "#" + pointer + "/" + OciJson.PointerToken(name);
        }
        return output;
    }
}
