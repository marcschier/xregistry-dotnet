using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed record ValidationVersion(Entity Entity, RegistryJson Metadata);
    private sealed record ValidationResource(Entity Entity, RegistryJson Meta, List<ValidationVersion> Versions);

    private sealed partial class Request
    {
        private readonly List<ValidationResource> _validationResources = [];
        private readonly List<Uri> _documentReferences = [];
        private bool _requireEmptyBody;

        private async ValueTask ValidateResourcesAsync()
        {
            var offeredFormats = _engine._options.ResourceValidator?.Formats ?? [];
            var resourceChecks = _engine._options.ResourceValidator is null ? 0 : _validationResources.Count(resource =>
                ResourceDefinition(RegistryPath.Parse(resource.Entity.Key)).ValidateFormat &&
                resource.Versions.Any(version => version.Metadata.RootElement.TryGetProperty("format", out _) &&
                    !IsOpaqueOpenUsd(ResourceDefinition(RegistryPath.Parse(resource.Entity.Key)), version.Metadata.RootElement)));
            if (resourceChecks > _engine.Limits.MaxSchemaSteps)
            {
                throw ServerErrors.Create("operation_limit", _operation.Path.EscapedPath, "The schema work budget cannot fund every Resource validation.");
            }

            var validationLimits = _engine.Limits with
            {
                MaxSchemaSteps = _engine.Limits.MaxSchemaSteps / Math.Max(1, resourceChecks)
            };
            foreach (var resource in _validationResources)
            {
                var path = RegistryPath.Parse(resource.Entity.Key);
                var definition = ResourceDefinition(path);
                var hasOpaque = resource.Versions.Any(version => IsOpaqueOpenUsd(definition, version.Metadata.RootElement));
                IReadOnlyDictionary<string, RegistryVersionValidation>? results = null;
                if (definition.ValidateFormat && _engine._options.ResourceValidator is { } validator &&
                    resource.Versions.Any(version => !IsOpaqueOpenUsd(definition, version.Metadata.RootElement) &&
                        version.Metadata.RootElement.TryGetProperty("format", out var format) &&
                        (_capabilities.Formats.Contains(format.GetString()!) ||
                            !offeredFormats.Contains(format.GetString()!, StringComparer.OrdinalIgnoreCase))))
                {
                    var versions = new List<RegistryVersionCandidate>();
                    foreach (var version in resource.Versions)
                    {
                        Work();
                        if (IsOpaqueOpenUsd(definition, version.Metadata.RootElement)) { continue; }
                        var external = ServerJson.Text(version.Entity.Attributes, definition.Singular + "url");
                        var bytes = !definition.HasDocument || external is not null ? [] : await ReadDocumentAsync(version.Entity).ConfigureAwait(false);
                        versions.Add(new(Id(version.Entity), version.Metadata, new(bytes),
                            external is null ? null : DocumentReference(version.Entity.Key, external)));
                    }

                    var context = new RegistryResourceValidationContext(definition, _model.Groups[path.GroupType!],
                        ServerJson.Own(Require(ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!))).Attributes, _engine.Limits.Json),
                        resource.Meta, versions.AsReadOnly(), validationLimits)
                    {
                        EnabledFormats = _capabilities.Formats,
                        EnabledCompatibilities = _capabilities.Compatibilities
                    };
                    results = await _engine.ExternalCallAsync(ct => validator.ValidateAsync(context, ct), path.EscapedPath, _ct).ConfigureAwait(false);

                    if (results.Count != versions.Count || versions.Any(version => !results.ContainsKey(version.VersionId)))
                    {
                        throw new InvalidOperationException("The resource validator did not return exactly one result per candidate Version.");
                    }
                }

                foreach (var candidate in resource.Versions)
                {
                    var entity = candidate.Entity;
                    var hasFormat = candidate.Metadata.RootElement.TryGetProperty("format", out _);
                    var compatibility = resource.Meta.RootElement.TryGetProperty("compatibility", out _);
                    var external = candidate.Metadata.RootElement.TryGetProperty(definition.Singular + "url", out _);
                    var opaque = IsOpaqueOpenUsd(definition, candidate.Metadata.RootElement);
                    var outcome = results?.GetValueOrDefault(Id(entity)) ??
                        new RegistryVersionValidation(RegistryValidationStatus.Unsupported, RegistryValidationStatus.Unsupported);
                    if (hasFormat && !_capabilities.Formats.Contains(candidate.Metadata.RootElement.GetProperty("format").GetString()!) &&
                        (offeredFormats.Contains(candidate.Metadata.RootElement.GetProperty("format").GetString()!, StringComparer.OrdinalIgnoreCase) ||
                            outcome.Format is not (RegistryValidationStatus.Unsupported or RegistryValidationStatus.Indeterminate)))
                    {
                        outcome = new(RegistryValidationStatus.Unsupported, RegistryValidationStatus.Unsupported,
                            "The Version format is not enabled.", "The Version format is not enabled.");
                    }
                    else if (hasFormat && compatibility && !_capabilities.Compatibility(
                        candidate.Metadata.RootElement.GetProperty("format").GetString()!,
                        resource.Meta.RootElement.GetProperty("compatibility").GetString()!))
                    {
                        outcome = outcome with { Compatibility = RegistryValidationStatus.Unsupported, CompatibilityReason = "The compatibility mode is not enabled for this format." };
                    }
                    var before = (JsonObject)entity.Attributes.DeepClone();
                    if (hasOpaque && compatibility)
                    {
                        outcome = outcome with
                        {
                            Compatibility = RegistryValidationStatus.Unsupported,
                            CompatibilityReason = "Opaque artifacts are not included in structural format or compatibility validation."
                        };
                    }
                    var formatValid = ApplyValidation(entity, "format", definition.ValidateFormat && hasFormat && !opaque, outcome.Format,
                        outcome.FormatReason, definition.StrictValidation, external, resource.Meta);
                    ApplyValidation(entity, "compatibility", definition.ValidateCompatibility && hasFormat && compatibility && !opaque,
                        formatValid ? outcome.Compatibility : RegistryValidationStatus.Unsupported,
                        formatValid ? outcome.CompatibilityReason : "The Version's format has not been validated.",
                        definition.StrictValidation, external, resource.Meta);
                    if (!entity.Deleted && !ServerJson.Equal(before, entity.Attributes))
                    {
                        entity.StorageDirty = true;
                    }
                }
            }

            foreach (var entity in _entities.Values.Where(static entity => entity.Obligations.Count != 0).ToArray())
            {
                var external = entity.Obligations.Where(static obligation => obligation.Kind != "matchversions").ToArray();
                if (external.Length == 0)
                {
                    continue;
                }

                var validator = _engine._options.ObligationValidator ??
                    throw ServerErrors.Create("obligation_policy_required", entity.Key, "The host has no validator for external metadata obligations.");
                await RunPolicyAsync(ct => validator.ValidateAsync(new(RegistryPath.Parse(entity.Key),
                    ServerJson.Own(entity.Attributes, _engine.Limits.Json), _model, _engine.PublicRoot, _context.Caller,
                    external, _engine.Limits), ct)).ConfigureAwait(false);
            }
        }

        private static bool ApplyValidation(Entity entity, string aspect, bool requested, RegistryValidationStatus status,
            string? reason, bool strict, bool external, RegistryJson meta)
        {
            var flag = aspect + "validated";
            var reasonField = flag + "reason";
            entity.Attributes.Remove(flag);
            entity.Attributes.Remove(reasonField);
            if (!requested)
            {
                return false;
            }

            if (!Enum.IsDefined(status) || status == RegistryValidationStatus.NotRequested)
            {
                throw new InvalidOperationException("A validator omitted a model-required validation outcome.");
            }

            if (status == RegistryValidationStatus.Invalid)
            {
                throw ValidationError(aspect + "_violation", entity, meta, reason ?? "The Version violates the requested validation rule.");
            }

            if (external || status is RegistryValidationStatus.Unsupported or RegistryValidationStatus.Indeterminate)
            {
                if (strict)
                {
                    throw ValidationError(external ? "format_external" : aspect + "_unknown", entity, meta,
                        reason ?? "The requested validation cannot be established.");
                }

                entity.Attributes[flag] = false;
                entity.Attributes[reasonField] = !string.IsNullOrWhiteSpace(reason) ? reason :
                    external ? "The Document is external and has not been fetched." : "The requested " + aspect + " validation is unsupported or indeterminate.";
                return false;
            }

            entity.Attributes[flag] = true;
            return true;
        }

        private static RegistryException ValidationError(string code, Entity version, RegistryJson meta, string detail)
        {
            var subject = code.StartsWith("compatibility_", StringComparison.Ordinal)
                ? ResourceKey(RegistryPath.Parse(version.Key)) : version.Key;
            return ServerErrors.With(code, subject, detail,
                ("format", ServerJson.Text(version.Attributes, "format") ?? ""),
                ("compat", meta.RootElement.TryGetProperty("compatibility", out var value) ? value.GetString() ?? "" : ""));
        }

        private async ValueTask RunPolicyAsync(Func<CancellationToken, ValueTask> action)
        {
            await _engine.ExternalCallAsync(async ct =>
            {
                await action(ct).ConfigureAwait(false);
                return true;
            }, _operation.Path.EscapedPath, _ct).ConfigureAwait(false);
        }

        private Uri DocumentReference(string key, string value) => new(new Uri(_engine.Url(key)), value);

        private ValueTask AuthorizeDocumentReferenceAsync(Uri reference)
        {
            var policy = _engine._options.DocumentReferencePolicy ??
                throw ServerErrors.Create("document_reference_policy_required", _operation.Path.EscapedPath, "External Document references require an explicit host policy.");
            return RunPolicyAsync(ct => policy.AuthorizeAsync(_context.Caller, reference, ct));
        }
    }
}
