using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private const string ShortPrefix = "/~/";
    private const string ShortKeys = "$short/";

    /// <summary>Resolves an engine-owned short entity path, or parses an ordinary Registry-relative path.</summary>
    /// <remarks>Only the reserved /~/ token namespace is translated. Domain URL strings and Document bytes are never rewritten.</remarks>
    public async ValueTask<RegistryPath> ResolvePathAsync(RegistryAction action, string escapedPath,
        RegistryOperationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(escapedPath);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!escapedPath.StartsWith(ShortPrefix, StringComparison.Ordinal))
        {
            return RegistryPath.Parse(escapedPath);
        }

        if (escapedPath.Length > 8192 || escapedPath.Contains('?', StringComparison.Ordinal) || escapedPath.Contains('#', StringComparison.Ordinal))
        {
            throw ServerErrors.Create("malformed_path", escapedPath, "A short path must be bounded and contain no query or fragment.");
        }

        var read = action is RegistryAction.Read or RegistryAction.Head or RegistryAction.Options;
        if (!read && IsReadOnly)
        {
            throw ServerErrors.Create("readonly", escapedPath, "The persistence backend is read-only.");
        }

        if (!context.Caller.Identities.Any(static identity => identity.IsAuthenticated) && (!read || !_options.AllowAnonymousReads))
        {
            throw ServerErrors.Create("unauthorized", escapedPath, "Authentication is required.");
        }

        var details = escapedPath.EndsWith("$details", StringComparison.Ordinal);
        var path = details ? escapedPath[..^8] : escapedPath;
        var slash = path.IndexOf('/', ShortPrefix.Length);
        var token = slash < 0 ? path[ShortPrefix.Length..] : path[ShortPrefix.Length..slash];
        if (token != "0" && (token.Length != 16 || token.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))))
        {
            throw ServerErrors.Create("not_found", escapedPath, "The short entity identifier is not recognized.");
        }

        using var snapshot = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var record = snapshot.Find(ShortKeys + token) ??
            throw ServerErrors.Create("not_found", escapedPath, "The short entity identifier does not exist.");
        var xid = record.Metadata.RootElement.GetProperty("xid").GetString()!;
        var suffix = slash < 0 ? "" : path[slash..];
        var canonical = (xid == "/" && suffix.Length != 0 ? "" : xid) + suffix + (details ? "$details" : "");
        var resolved = RegistryPath.Parse(canonical);
        _ = Allowed(resolved, LoadModel(snapshot).Model);
        await AuthorizeRequestAsync(action, resolved, context, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    private sealed partial class Request
    {
        private readonly Dictionary<string, RegistryMutation> _shortMutations = new(StringComparer.Ordinal);

        private void PrepareShortIdentifiers()
        {
            if (!_capabilities.ShortSelf)
            {
                return;
            }

            EnsureShortIdentifier(Require("/"));
            if (!_previousCapabilities.ShortSelf)
            {
                foreach (var groupType in _model.Groups.Values)
                {
                    foreach (var group in Children("/" + groupType.Plural))
                    {
                        EnsureShortIdentifier(group);
                        foreach (var resourceType in groupType.Resources.Keys)
                        {
                            foreach (var resource in Children(group.Key + "/" + resourceType))
                            {
                                EnsureShortIdentifier(resource);
                            }
                        }
                    }
                }
            }

            foreach (var entity in _entities.Values.Where(static entity => !entity.Deleted && entity.ShortId is null).ToArray())
            {
                if (RegistryPath.Parse(entity.Key).Kind is RegistryPathKind.Group or RegistryPathKind.Resource)
                {
                    EnsureShortIdentifier(entity);
                }
            }
        }

        private void EnsureShortIdentifier(Entity entity)
        {
            if (entity.ShortId is not null)
            {
                return;
            }

            Work();
            var token = "0";
            if (entity.Key != "/")
            {
                do
                {
                    Work();
                    token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');
                }
                while (_snapshot.Find(ShortKeys + token) is not null || _shortMutations.ContainsKey(ShortKeys + token));
            }

            entity.ShortId = token;
            entity.StorageDirty = true;
            _shortMutations.Add(ShortKeys + token, RegistryMutation.Put(ShortKeys + token,
                ServerJson.Own(new JsonObject { ["xid"] = entity.Key }, _engine.Limits.Json)));
        }

        private string ShortSelf(RegistryPath path)
        {
            var key = ServerJson.Key(path);
            var suffix = "";
            if (path.Kind is RegistryPathKind.Meta or RegistryPathKind.Version)
            {
                key = ResourceKey(path);
                suffix = path.Kind == RegistryPathKind.Meta ? "/meta" : "/versions/" + Uri.EscapeDataString(path.VersionId!.Value);
            }

            var token = Require(key).ShortId;
            if (token is null)
            {
                throw new InvalidDataException("An enabled shortself representation has no persisted entity identifier.");
            }

            return _engine.Url(ShortPrefix + token + suffix);
        }
    }
}
