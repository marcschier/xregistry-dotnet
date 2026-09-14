namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private readonly HashSet<string> _ignoredReadonly = new(StringComparer.Ordinal);

        private bool IgnoreReadonly(RegistryPath resource)
        {
            if (!_flags.Ignore.Contains("readonly") || Find(ResourceKey(resource)) is not { } entity ||
                !ServerJson.Boolean(entity.Attributes, "readonly"))
            {
                return false;
            }

            _ignoredReadonly.Add(entity.Key);
            return true;
        }

        private void CheckReadonlyTarget()
        {
            if (_operation.Path.ResourceId is not null && IgnoreReadonly(_operation.Path))
            {
                throw ServerErrors.With("bad_flag", _operation.Path.EscapedPath,
                    "Ignoring the only targeted readonly Resource would invalidate the request.", ("flag", "ignore"));
            }
        }

        private void CheckReadonlyEffects()
        {
            foreach (var entity in _entities.Values.Where(static entity => entity.Dirty).ToArray())
            {
                var path = RegistryPath.Parse(entity.Key);
                if (path.ResourceId is null)
                {
                    continue;
                }

                var key = ResourceKey(path);
                var resource = _entities.GetValueOrDefault(key) ?? Find(key);
                if (resource is not null && ServerJson.Boolean(resource.Original, "readonly"))
                {
                    throw ServerErrors.Create("readonly", key, "A readonly Resource cannot be modified by indirect lifecycle or constraint effects.");
                }
            }
        }
    }
}
