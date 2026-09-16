// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using XRegistry.Models;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private async ValueTask ValidateCrossReferenceConstraintsAsync()
        {
            if (_resources.Count == 0 && !_modelChanged)
            {
                return;
            }

            var affectedTypes = new HashSet<RegistryResourceDefinition>(ReferenceEqualityComparer.Instance);
            foreach (var key in _resources)
            {
                Work();
                var path = RegistryPath.Parse(key);
                if (_model.Groups.TryGetValue(path.GroupType!, out var group) &&
                    group.Resources.TryGetValue(path.ResourceType!, out var resource))
                {
                    affectedTypes.Add(resource);
                }
            }

            var authorizedTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var groupType in _model.Groups.Values)
            {
                Work();
                var affected = _modelChanged;
                foreach (var resource in groupType.Resources.Values)
                {
                    Work();
                    if (affectedTypes.Contains(resource))
                    {
                        affected = true;
                        break;
                    }
                }
                if (!affected)
                {
                    continue;
                }

                foreach (var group in Children("/" + groupType.Plural))
                {
                    Work();
                    var domainSelectors = ServerJson.Text(group.Attributes, "protocol") is not null ||
                        ServerJson.Text(group.Attributes, "envelope") is not null;
                    var domainContract = domainSelectors && groupType.Resources.Values.Any(resource =>
                        RegistryDomainRules.HasMessageGroupContract(groupType, resource));
                    if (groupType.Constraints.Count == 0 && group.Attributes["constraints"] is not JsonObject { Count: > 0 } &&
                        !domainContract)
                    {
                        continue;
                    }

                    var groupDefinition = GroupForValidation(RegistryPath.Parse(group.Key));
                    var constrainedTypes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var constraint in groupDefinition.Constraints.Values)
                    {
                        Work();
                        if ((constraint.EnumValues.Count != 0 || constraint.EqualsAttribute is not null) &&
                            (_modelChanged || affectedTypes.Contains(groupType.Resources[constraint.ResourceType])))
                        {
                            constrainedTypes.Add(constraint.ResourceType);
                        }
                    }
                    if (domainContract)
                    {
                        foreach (var resource in groupType.Resources.Values)
                        {
                            Work();
                            if ((_modelChanged || affectedTypes.Contains(resource)) &&
                                RegistryDomainRules.HasMessageGroupContract(groupType, resource))
                            {
                                constrainedTypes.Add(resource.Plural);
                            }
                        }
                    }

                    RegistryJson? groupMetadata = null;
                    var groupBytes = 0;
                    foreach (var resourceType in constrainedTypes)
                    {
                        foreach (var alias in Children(group.Key + "/" + resourceType))
                        {
                            Work();
                            if (ServerJson.Text(alias.Attributes, "xref") is not { } targetKey)
                            {
                                continue;
                            }

                            var targetPath = CrossReferencePath(RegistryPath.Parse(alias.Key), targetKey);
                            targetKey = ServerJson.Key(targetPath);
                            var forward = _modelChanged || _resources.Contains(alias.Key);
                            if (!forward && !_resources.Contains(targetKey))
                            {
                                continue;
                            }

                            if (forward && !authorizedTargets.Contains(targetKey))
                            {
                                await AuthorizeConstraintReadAsync(targetPath, alias.Key).ConfigureAwait(false);
                                await AuthorizeConstraintReadAsync(RegistryPath.Parse(targetKey + "/meta"), alias.Key).ConfigureAwait(false);
                            }

                            var target = Find(targetKey);
                            if (target is null || target.Attributes["xref"] is not null)
                            {
                                continue;
                            }

                            var versions = Children(targetKey + "/versions");
                            if (forward && authorizedTargets.Add(targetKey))
                            {
                                foreach (var version in versions)
                                {
                                    await AuthorizeConstraintReadAsync(RegistryPath.Parse(version.Key), alias.Key).ConfigureAwait(false);
                                }
                            }

                            var definition = groupDefinition.Resources[resourceType];
                            if (groupMetadata is null)
                            {
                                groupMetadata = ServerJson.Own(group.Attributes, _engine.Limits.Json);
                                groupBytes = Encoding.UTF8.GetByteCount(groupMetadata.RootElement.GetRawText());
                            }
                            var defaultId = ServerJson.Text(target.Attributes, "defaultversionid");
                            foreach (var version in versions)
                            {
                                Work();
                                ChargeCrossReferenceConstraintWork(groupDefinition, definition);
                                WorkingBytes(3L * (groupBytes + Encoding.UTF8.GetByteCount(version.Attributes.ToJsonString())));
                                var projected = (JsonObject)version.Attributes.DeepClone();
                                var versionKey = alias.Key + "/versions/" + Uri.EscapeDataString(Id(version));
                                projected[definition.Singular + "id"] = Id(alias);
                                projected["xid"] = versionKey;
                                projected["self"] = _engine.Url(versionKey, definition.HasDocument);
                                projected["isdefault"] = Id(version) == defaultId;
                                try
                                {
                                    var projectedMetadata = ServerJson.Own(projected, _engine.Limits.Json);
                                    RegistryMetadataValidator.ValidateGroupConstraints(
                                        projectedMetadata, groupDefinition, definition,
                                        groupMetadata.RootElement, _engine.Limits.Json, _ct);
                                    RegistryDomainRules.ValidateMessageGroupConstraints(projectedMetadata.RootElement,
                                        groupDefinition, definition, groupMetadata.RootElement, _ct);
                                }
                                catch (RegistryException exception) when (exception.Diagnostic.Code is "constraint_failure" or "invalid_attribute")
                                {
                                    if (_modelChanged)
                                    {
                                        throw new RegistryException(new("model_compliance_error", "/model",
                                            "An existing cross-reference violates the proposed model's Group constraints."), exception);
                                    }

                                    // Reverse validation must not reveal the identity of an otherwise unreadable referring Group.
                                    throw MetadataError(exception, forward ? alias.Key : targetKey, versionConstraints: true);
                                }
                            }
                        }
                    }
                }
            }
        }

        private async ValueTask AuthorizeConstraintReadAsync(RegistryPath target, string subject)
        {
            Work();
            if (!await _engine.ExternalCallAsync(ct =>
                _engine._authorization.AuthorizeAsync(_context.Caller, RegistryAccess.Read, target, ct),
                subject, _ct).ConfigureAwait(false))
            {
                throw ServerErrors.Create("forbidden", subject, "The caller cannot read all metadata needed to validate the cross-reference.");
            }
        }

        private void ChargeCrossReferenceConstraintWork(RegistryGroupDefinition group, RegistryResourceDefinition resource)
        {
            foreach (var constraint in group.Constraints.Values)
            {
                Work();
                if (constraint.ResourceType != resource.Plural)
                {
                    continue;
                }

                var maximum = 2L * constraint.AttributePath.Count - 1 + constraint.EnumValues.Count +
                    constraint.EqualsPath.Count + (constraint.EqualsAttribute is null ? 0 : 1);
                for (long step = 0; step < maximum; step++)
                {
                    Work();
                }
            }
        }
    }
}
