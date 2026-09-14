namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private sealed class CollectionState
        {
            internal bool Loaded { get; set; }
            internal HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
            internal HashSet<string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
            internal HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);
            internal IReadOnlyList<Entity>? Entities { get; set; }
        }

        private readonly Dictionary<string, CollectionState> _collections = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RegistryRecord> _collectionRecords = new(StringComparer.Ordinal);

        private CollectionState Collection(string key)
        {
            Work();
            var state = CollectionStateFor(key);
            if (!state.Loaded)
            {
                foreach (var record in _snapshot.GetChildren(key))
                {
                    Work();
                    WorkingBytes(64L + 2L * System.Text.Encoding.UTF8.GetByteCount(record.Key) +
                        System.Text.Encoding.UTF8.GetByteCount(record.Metadata.RootElement.GetRawText()));
                    _collectionRecords.Add(record.Key, record);
                    if (!state.Deleted.Contains(record.Key))
                    {
                        if (!state.Keys.Add(record.Key) || !state.Ids.Add(LeafId(record.Key)))
                        {
                            throw new InvalidDataException("The stored collection has conflicting sibling identities.");
                        }
                    }
                }

                state.Loaded = true;
            }

            return state;
        }

        private CollectionState CollectionStateFor(string key)
        {
            if (!_collections.TryGetValue(key, out var state))
            {
                state = new();
                _collections.Add(key, state);
            }

            return state;
        }

        private IReadOnlyList<Entity> Children(string key)
        {
            var state = Collection(key);
            if (state.Entities is null)
            {
                var entities = new List<Entity>(state.Keys.Count);
                foreach (var member in state.Keys.Order(StringComparer.Ordinal))
                {
                    Work();
                    entities.Add(Require(member));
                }

                state.Entities = entities.AsReadOnly();
            }

            return state.Entities;
        }

        private int ChildCount(string key) => Collection(key).Keys.Count;

        private RegistryRecord? StoredRecord(string key)
        {
            if (_collectionRecords.TryGetValue(key, out var record))
            {
                return record;
            }

            return _collections.TryGetValue(Parent(key), out var collection) && collection.Loaded &&
                !collection.Keys.Contains(key) ? null : _snapshot.Find(key);
        }

        private static void AddMember(CollectionState collection, string key)
        {
            collection.Keys.Add(key);
            collection.Ids.Add(LeafId(key));
            collection.Deleted.Remove(key);
            collection.Entities = null;
        }

        private void RemoveMember(string key)
        {
            var collection = CollectionStateFor(Parent(key));
            collection.Keys.Remove(key);
            collection.Ids.Remove(LeafId(key));
            collection.Deleted.Add(key);
            collection.Entities = null;
        }

        private bool WasDeleted(string key) => _entities.TryGetValue(key, out var entity) && entity.Deleted;
    }
}
