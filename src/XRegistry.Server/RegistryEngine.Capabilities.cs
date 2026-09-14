using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private const string CapabilitiesKey = "$capabilities";
    private CapabilityProfile? _capabilityCache;
    private bool SupportsShortSelf => !PublicRoot.AbsoluteUri.Contains('$', StringComparison.Ordinal);

    private CapabilityProfile LoadCapabilities(IRegistrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var record = snapshot.Find(CapabilitiesKey);
        if (record is null)
        {
            return new(DefaultCapabilities(), "initial");
        }

        var revision = record.Metadata.RootElement.GetProperty("revision").GetString()!;
        var cached = Volatile.Read(ref _capabilityCache);
        if (cached?.Revision == revision)
        {
            return cached;
        }

        var metadata = ServerJson.Object(RegistryJson.FromElement(record.Metadata.RootElement.GetProperty("capabilities"), Limits.Json));
        metadata["available"]!["capabilities"]!["mutable"] = _options.AllowCapabilityUpdates && !IsReadOnly;
        if (IsReadOnly)
        {
            foreach (var entry in metadata["available"]!.AsObject())
            {
                entry.Value!["mutable"] = false;
            }
        }

        try
        {
            var defaults = DefaultCapabilities();
            var validated = CapabilityProfile.Update(new(defaults, "initial"), defaults, metadata, patch: false,
                SupportsShortSelf, Limits.Json, Limits.MaxEntityOperations, cancellationToken);
            cached = new(validated.Metadata, revision);
        }
        catch (RegistryException exception)
        {
            throw new InvalidDataException("The persisted capability profile is incompatible with this host's offered capabilities.", exception);
        }

        Volatile.Write(ref _capabilityCache, cached);
        return cached;
    }

    private static string AvailableKind(RegistryPath path) => path.Kind switch
    {
        RegistryPathKind.Capabilities => "capabilities",
        RegistryPathKind.CapabilitiesOffered => "capabilitiesoffered",
        RegistryPathKind.Model => "model",
        RegistryPathKind.ModelSource => "modelsource",
        RegistryPathKind.Export => "export",
        _ => "entities"
    };

    private static void CheckAvailability(RegistryPath path, RegistryAction action, CapabilityProfile profile)
    {
        var kind = AvailableKind(path);
        if (!profile.Has(kind))
        {
            throw ServerErrors.Create("not_available", kind, "The requested metadata is not enabled.");
        }

        if (action is RegistryAction.Read or RegistryAction.Head or RegistryAction.Options)
        {
            return;
        }

        if (!profile.Mutable(kind) && !(path.Kind == RegistryPathKind.Registry &&
            action is RegistryAction.Replace or RegistryAction.Patch &&
            (profile.Mutable("capabilities") || profile.Mutable("modelsource"))))
        {
            throw ServerErrors.Create("readonly", path.EscapedPath, "This metadata is not mutable in the enabled capability profile.");
        }
    }

    private static void CheckVersionModes(RegistryModel model, CapabilityProfile profile)
    {
        if (model.Groups.Values.SelectMany(static group => group.Resources.Values)
            .Any(resource => !profile.VersionModes.Contains(resource.VersionMode)))
        {
            throw ServerErrors.Create("capability_error", "/capabilities", "The model uses a Version mode that the requested profile disables.");
        }
    }

    private static List<RegistryAction> EffectiveAllowed(RegistryPath path, List<RegistryAction> actions, CapabilityProfile profile) =>
        actions.Where(action => action is RegistryAction.Read or RegistryAction.Head or RegistryAction.Options ||
            profile.Mutable(AvailableKind(path)) || path.Kind == RegistryPathKind.Registry &&
            action is RegistryAction.Replace or RegistryAction.Patch &&
            (profile.Mutable("capabilities") || profile.Mutable("modelsource"))).ToList();

    private sealed partial class Request
    {
        private readonly CapabilityProfile _previousCapabilities;
        private CapabilityProfile _capabilities;
        private bool _capabilitiesChanged;

        private async ValueTask ApplyCapabilitiesAsync(JsonObject? value, bool patch)
        {
            CheckAvailability(RegistryPath.Parse("/capabilities"), RegistryAction.Patch, _previousCapabilities);
            await _engine.AuthorizeAsync(_context, RegistryPath.Parse("/capabilities"), RegistryAccess.UpdateCapabilities, _ct).ConfigureAwait(false);
            _capabilities = CapabilityProfile.Update(_capabilities, _engine.DefaultCapabilities(), value, patch,
                _engine.SupportsShortSelf, _engine.Limits.Json, _engine.Limits.MaxEntityOperations, _ct);
            _capabilitiesChanged = true;
            Touch(Require("/"));
        }
    }
}
