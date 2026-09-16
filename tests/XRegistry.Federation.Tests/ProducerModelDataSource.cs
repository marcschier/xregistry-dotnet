// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Federation.Tests;

internal sealed class ProducerModelDataSource(string name, RegistryModel model) : IFederationReadSource
{
    internal Dictionary<string, JsonObject> Entities { get; } = new(StringComparer.Ordinal);
    internal List<FederationReadRequest> Reads { get; } = [];
    internal HashSet<string> Denied { get; } = new(StringComparer.Ordinal);
    internal bool ProducerOwned { get; init; }
    internal bool Complete { get; set; } = true;
    internal bool CaptureVersions { get; init; }
    internal FederationRepresentation Representation { get; init; } = FederationRepresentation.ApiView;
    internal int Opens { get; private set; }
    internal int Closes { get; private set; }
    public RegistryModel Model { get; } = model;
    public NativeRegistryContext Context { get; } = new("http", "https://" + name + ".test/registry");
    public JsonElement Capabilities => RegistryJson.Parse(ProducerOwned
        ? """{"federation":{"resolution":"producer"}}""" : "{}").RootElement;

    internal FederationSourceRegistration Registration() => new(name, Representation, (_, _) =>
    {
        Opens++;
        return ValueTask.FromResult(new FederationSourceLease(this, () =>
        {
            Closes++;
            return ValueTask.CompletedTask;
        }));
    });

    internal void AddGroup(string id, string attributes = "{}")
    {
        var path = "/gs/" + id;
        var group = Common(path, "gid", id);
        Merge(group, JsonNode.Parse(attributes)!.AsObject());
        group["rsurl"] = Context.Source + path + "/rs";
        group["rscount"] = 0;
        Entities.Add(path, group);
    }

    internal void AddResource(string group, string id, IReadOnlyDictionary<string, string> versions, string defaultId = "v1")
    {
        var owner = "/gs/" + group + "/rs/" + id;
        var resource = new JsonObject
        {
            ["rid"] = id,
            ["xid"] = owner,
            ["self"] = Context.Source + owner + "$details",
            ["metaurl"] = Context.Source + owner + "/meta",
            ["versionsurl"] = Context.Source + owner + "/versions",
            ["versionscount"] = versions.Count,
        };
        Entities.Add(owner, resource);
        var meta = Common(owner + "/meta", "rid", id);
        meta["readonly"] = false;
        meta["defaultversionsticky"] = false;
        meta["defaultversionid"] = defaultId;
        meta["defaultversionurl"] = Context.Source + owner + "/versions/" + defaultId + "$details";
        Entities.Add(owner + "/meta", meta);
        foreach (var (versionId, attributes) in versions)
        {
            var version = Common(owner + "/versions/" + versionId, "rid", id);
            version["versionid"] = versionId;
            version["ancestorid"] = defaultId;
            version["isdefault"] = versionId == defaultId;
            version["contenttype"] = "application/octet-stream";
            Merge(version, JsonNode.Parse(attributes)!.AsObject());
            Entities.Add(owner + "/versions/" + versionId, version);
        }
        Entities["/gs/" + group]["rscount"] = Entities.Keys.Count(key =>
            key.StartsWith("/gs/" + group + "/rs/", StringComparison.Ordinal) && key.Count(c => c == '/') == 4);
    }

    internal void AddAlias(string group, string id, string target)
    {
        var owner = "/gs/" + group + "/rs/" + id;
        Entities.Add(owner, new JsonObject
        {
            ["rid"] = id,
            ["xid"] = owner,
            ["self"] = Context.Source + owner + "$details",
            ["metaurl"] = Context.Source + owner + "/meta",
        });
        Entities.Add(owner + "/meta", new JsonObject
        {
            ["rid"] = id,
            ["xid"] = owner + "/meta",
            ["self"] = Context.Source + owner + "/meta",
            ["xref"] = target,
        });
        Entities["/gs/" + group]["rscount"] = Entities["/gs/" + group]["rscount"]!.GetValue<int>() + 1;
    }

    public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add(request);
        if (Denied.Contains(request.Target)) { throw new FederationException(FederationErrorCode.PolicyDenied, "The metadata path is private."); }
        if (request.Operation == FederationOperation.Collection)
        {
            var children = new JsonObject();
            foreach (var path in Entities.Keys.Where(key => key.StartsWith(request.Target + "/", StringComparison.Ordinal) &&
                key.Count(c => c == '/') == request.Target.Count(c => c == '/') + 1))
            {
                children[path[(path.LastIndexOf('/') + 1)..]] = Entity(path, request.Representation);
            }
            return ValueTask.FromResult(Metadata(request.Target, new JsonObject
            {
                ["complete"] = Complete,
                ["entities"] = children,
                ["xid"] = request.Target,
            }));
        }
        if (request.Operation == FederationOperation.Document)
        {
            var selected = request.Target;
            if (RegistryPath.Parse(selected).Kind == RegistryPathKind.Resource)
            {
                if (!Entities.TryGetValue(selected + "/meta", out var meta)) { throw Missing(); }
                selected += "/versions/" + meta["defaultversionid"]!.GetValue<string>();
            }
            if (!Entities.ContainsKey(selected)) { throw Missing(); }
            return ValueTask.FromResult(FederationReadResult.FromDocument(selected, Context,
                new FederationDocument([0, 255, 13, 10, 65], "application/octet-stream")));
        }
        return ValueTask.FromResult(Metadata(request.Target, new JsonObject { ["entity"] = Entity(request.Target, request.Representation) }));
    }

    private JsonObject Entity(string target, FederationRepresentation representation)
    {
        if (target == "/")
        {
            var root = Common("/", "registryid", name);
            root["specversion"] = "1.0-rc4";
            root["gsurl"] = Context.Source + "/gs";
            root["gscount"] = Entities.Keys.Count(key => key.Count(c => c == '/') == 2);
            return root;
        }
        if (!Entities.TryGetValue(target, out var stored)) { throw Missing(); }
        var value = stored.DeepClone().AsObject();
        if (RegistryPath.Parse(target).Kind != RegistryPathKind.Resource) { return value; }
        var meta = Entities[target + "/meta"];
        if (meta.ContainsKey("xref"))
        {
            value["meta"] = meta.DeepClone();
            return value;
        }
        if (representation == FederationRepresentation.ApiView)
        {
            var version = Entities[target + "/versions/" + meta["defaultversionid"]!.GetValue<string>()];
            foreach (var property in version)
            {
                if (property.Key is not ("self" or "xid")) { value[property.Key] = property.Value?.DeepClone(); }
            }
        }
        if (CaptureVersions || representation == FederationRepresentation.DocumentView)
        {
            value["meta"] = meta.DeepClone();
            var versions = new JsonObject();
            foreach (var pair in Entities.Where(pair => pair.Key.StartsWith(target + "/versions/", StringComparison.Ordinal)))
            {
                versions[pair.Key[(pair.Key.LastIndexOf('/') + 1)..]] = pair.Value.DeepClone();
            }
            value["versions"] = versions;
        }
        return value;
    }

    private JsonObject Common(string path, string idName, string id) => new()
    {
        ["xid"] = path,
        [idName] = id,
        ["self"] = Context.Source + (path == "/" ? "" : path),
        ["epoch"] = 1,
        ["createdat"] = "2026-09-14T00:00:00Z",
        ["modifiedat"] = "2026-09-14T00:00:00Z",
    };

    private static void Merge(JsonObject target, JsonObject attributes)
    {
        foreach (var item in attributes) { target[item.Key] = item.Value?.DeepClone(); }
    }

    private FederationReadResult Metadata(string target, JsonObject value) =>
        FederationReadResult.FromMetadata(target, Context, RegistryJson.Parse(value.ToJsonString()).RootElement);

    private static FederationException Missing() => new(FederationErrorCode.NotFound, "No exact source entity exists.");
}
