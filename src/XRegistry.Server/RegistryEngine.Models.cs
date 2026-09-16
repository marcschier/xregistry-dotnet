// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private async ValueTask ApplyModelAsync(RegistryJson source)
        {
            CheckAvailability(RegistryPath.Parse("/modelsource"), RegistryAction.Replace, _previousCapabilities);
            await _engine.AuthorizeAsync(_context, RegistryPath.Parse("/modelsource"), RegistryAccess.UpdateModel, _ct).ConfigureAwait(false);
            _ct.ThrowIfCancellationRequested();
            RegistryModel next;
            try
            {
                next = await _engine.ExternalCallAsync(_ => ValueTask.FromResult(
                    RegistryModel.Compile(source, _engine._options.ModelCompilation)), "/modelsource", _ct).ConfigureAwait(false);
            }
            catch (RegistryException exception) when (exception.Diagnostic.Code != "server_busy")
            {
                if (exception.Diagnostic.Code is "model_required_true" or "model_scalar_default")
                {
                    var parts = exception.Diagnostic.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var name = parts.Length >= 2 ? parts[^2].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal) : "";
                    throw ServerErrors.Wrap(exception, exception.Diagnostic.Code, "/model", ("name", name));
                }

                throw ServerErrors.Wrap(exception, "model_error", "/model");
            }

            _ = next.EffectiveModel;
            CheckVersionModes(next, _capabilities);
            _ct.ThrowIfCancellationRequested();
            var previous = _model;
            foreach (var (name, group) in previous.Groups)
            {
                Work();
                if (!next.Groups.TryGetValue(name, out var nextGroup))
                {
                    continue;
                }

                if (group.Singular != nextGroup.Singular)
                {
                    throw ServerErrors.Create("model_error", "/model", "An existing Group type's singular name is immutable.");
                }

                foreach (var (resourceName, resource) in group.Resources)
                {
                    Work();
                    if (nextGroup.Resources.TryGetValue(resourceName, out var nextResource) &&
                        resource.Singular != nextResource.Singular)
                    {
                        throw ServerErrors.Create("model_error", "/model", "An existing Resource type's singular name is immutable.");
                    }
                }
            }

            _model = next;
            _modelSource = source;
            _modelChanged = true;
            var root = Require("/");
            foreach (var name in previous.Groups.Keys.Except(next.Groups.Keys, StringComparer.Ordinal))
            {
                root.Attributes.Remove(name + "url");
                root.Attributes.Remove(name + "count");
            }

            Touch(root);
            var versionsToValidate = new List<Entity>();
            foreach (var record in _snapshot.EnumerateRecords())
            {
                Work();
                if (record.Key.StartsWith('$'))
                {
                    continue;
                }

                var path = RegistryPath.Parse(record.Key);
                if (path.GroupType is not null && !next.Groups.ContainsKey(path.GroupType) ||
                    path.ResourceType is not null && !next.Groups[path.GroupType!].Resources.ContainsKey(path.ResourceType))
                {
                    throw ServerErrors.Create("model_compliance_error", "/model", "An existing entity's type is absent from the proposed model.");
                }

                var entity = Require(record.Key);
                if (path.Kind == RegistryPathKind.Group)
                {
                    foreach (var name in previous.Groups[path.GroupType!].Resources.Keys.Except(next.Groups[path.GroupType!].Resources.Keys, StringComparer.Ordinal))
                    {
                        entity.Attributes.Remove(name + "url");
                        entity.Attributes.Remove(name + "count");
                    }
                }

                if (path.Kind == RegistryPathKind.Resource)
                {
                    _resources.Add(entity.Key);
                }
                else if (path.Kind == RegistryPathKind.Version)
                {
                    versionsToValidate.Add(entity);
                }

                if (path.Kind == RegistryPathKind.Version && entity.HadDocument && !ResourceDefinition(path).HasDocument)
                {
                    throw ServerErrors.With("hasdocument_violation", entity.Key, "Existing Versions have Documents that the new model would prohibit.",
                        ("plural", ResourceDefinition(path).Plural));
                }

                try
                {
                    if (path.Kind is RegistryPathKind.Group or RegistryPathKind.Registry)
                    {
                        Complete(entity);
                    }
                }

                catch (RegistryException exception) when (exception.Diagnostic.Code is "unknown_attribute" or "invalid_attribute" or "required_attribute_missing")
                {
                    throw new RegistryException(new("model_compliance_error", "/model", "Existing entity metadata does not comply with the proposed model."), exception);
                }
            }

            foreach (var version in versionsToValidate)
            {
                Work();
                try
                {
                    Complete(version);
                }
                catch (RegistryException exception) when (exception.Diagnostic.Code is
                    "unknown_attribute" or "invalid_attribute" or "required_attribute_missing" or "constraint_failure")
                {
                    throw new RegistryException(new("model_compliance_error", "/model",
                        "Existing Version metadata does not comply with the proposed model."), exception);
                }
            }
        }

        private JsonObject FreezeModel()
        {
            var frozen = ServerJson.Object(_model.EffectiveModel);
            var groups = frozen["groups"]!.AsObject();
            var seen = new Dictionary<RegistryResourceDefinition, string>(ReferenceEqualityComparer.Instance);
            foreach (var group in _model.Groups.Values.OrderBy(static group => group.Plural, StringComparer.Ordinal))
            {
                var frozenGroup = groups[group.Plural]!.AsObject();
                foreach (var resource in group.Resources.Values.OrderBy(static resource => resource.Plural, StringComparer.Ordinal))
                {
                    if (seen.TryGetValue(resource, out var canonicalType))
                    {
                        frozenGroup["resources"]!.AsObject().Remove(resource.Plural);
                        if (frozenGroup["ximportresources"] is not JsonArray imports)
                        {
                            imports = [];
                            frozenGroup["ximportresources"] = imports;
                        }

                        imports.Add((JsonNode?)JsonValue.Create(canonicalType));
                    }
                    else
                    {
                        seen.Add(resource, "/" + group.Plural + "/" + resource.Plural);
                    }
                }
            }

            _ = RegistryModel.Compile(ServerJson.Own(frozen, _engine._options.ModelCompilation.JsonLimits),
                _engine._options.ModelCompilation with { Resolver = null, SourceUri = null });
            return frozen;
        }
    }
}
