using System.Collections.Frozen;
using System.Text.Json;
using static XRegistry.Models.EndpointTemplateExpansion;

namespace XRegistry.Models;

/// <summary>An authored Endpoint definition and its independently owned, consumer-resolved metadata.</summary>
public sealed class EndpointDefinition
{
    private readonly FrozenDictionary<string, EndpointDefinitionMessage[]> _messageIndex;
    private readonly FrozenSet<string> _unresolvedMessageGroups;

    private EndpointDefinition(RegistryJson authored, RegistryJson resolved, string? subscriptionFilter,
        EndpointDefinitionEvaluation evaluation)
    {
        Authored = authored;
        Resolved = resolved;
        MqttSubscriptionFilter = subscriptionFilter;
        DeferredChecks = evaluation.DeferredChecks.AsReadOnly();
        Messages = evaluation.Messages.AsReadOnly();
        _unresolvedMessageGroups = evaluation.UnresolvedMessageGroups.ToFrozenSet(StringComparer.Ordinal);
        _messageIndex = Messages.GroupBy(static message => message.MessageId, StringComparer.Ordinal)
            .ToFrozenDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Gets the original metadata, without substitution or default injection.</summary>
    public RegistryJson Authored { get; }

    /// <summary>Gets metadata with protocol-option placeholders resolved.</summary>
    public RegistryJson Resolved { get; }

    /// <summary>Gets the canonical predefined protocol, the authored extension selector, or null when unspecified.</summary>
    public string? Protocol => EndpointDefinitionSemantics.Protocol(Resolved.RootElement);

    /// <summary>Gets the declared deployment flag or its normative true default; this is not a liveness claim.</summary>
    public bool IsDeployed => GetProtocolOption("deployed").GetBoolean();

    /// <summary>Gets the MQTT subscription filter, including a literal shared-group prefix, or null when not declared.</summary>
    public string? MqttSubscriptionFilter { get; }

    /// <summary>Gets obligations requiring external configuration, protocol-extension knowledge, or runtime evidence.</summary>
    public IReadOnlyList<RegistryDiagnostic> DeferredChecks { get; }

    /// <summary>Gets immutable Message candidates from inline and explicitly supplied referenced collections.</summary>
    public IReadOnlyList<EndpointDefinitionMessage> Messages { get; }

    /// <summary>Selects an exact Message ID without fetching, inheritance, or runtime-message inference.</summary>
    /// <remarks>
    /// Null searches the complete combined set; an empty Group reference selects the inline collection.
    /// Any other reference selects that exact supplied Group. Ambiguous, missing, and incomplete selections fail explicitly.
    /// </remarks>
    public EndpointDefinitionMessage SelectMessage(string messageId, string? groupReference = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        if (groupReference is null ? _unresolvedMessageGroups.Count != 0 : _unresolvedMessageGroups.Contains(groupReference))
        {
            throw Error("message_selection_incomplete", "/messagegroups", "The requested message set includes an unresolved collection.");
        }
        EndpointDefinitionMessage? selected = null;
        if (_messageIndex.TryGetValue(messageId, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (groupReference is not null && !string.Equals(groupReference, candidate.GroupReference, StringComparison.Ordinal))
                {
                    continue;
                }
                if (selected is not null)
                {
                    throw Error("ambiguous_message", "/messages", "The Message ID occurs in more than one collection; select an explicit Group reference.");
                }
                selected = candidate;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return selected ?? throw Error("message_not_found", "/messages", "The requested Message ID does not exist in the selected complete set.");
    }

    /// <summary>Gets a resolved option or its specified default; an unspecified option without a default is Undefined.</summary>
    public JsonElement GetProtocolOption(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return EndpointDefinitionSemantics.GetOption(Resolved.RootElement, name);
    }

    /// <summary>Checks bounded authored Endpoint metadata without bindings, substitution, defaults, or acquisition.</summary>
    /// <remarks>
    /// Applies common Endpoint rules, literal known option JSON kinds, structural record names, and Level-1
    /// expression syntax. String enums, numeric protocol constraints, addresses and other consumer-only
    /// obligations remain the responsibility of <see cref="Materialize"/>.
    /// </remarks>
    public static void ValidateAuthored(RegistryJson authored, EndpointTemplateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authored);
        ValidateAuthoredElement(authored.RootElement, options, cancellationToken);
    }

    internal static void ValidateAuthoredElement(JsonElement authored, EndpointTemplateOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new EndpointTemplateOptions();
        options.Validate();
        var bounded = RegistryJson.FromElement(authored, options.JsonLimits);
        cancellationToken.ThrowIfCancellationRequested();
        RegistryDomainRules.ValidateEndpointCommonMetadata(bounded.RootElement, cancellationToken);
        EndpointDefinitionSemantics.ValidateCommonEnvelopeOptions(bounded.RootElement);
        EndpointTemplateExpansion.ValidateAuthored(bounded, options, cancellationToken);
        EndpointDefinitionSemantics.ValidateOptionShapes(bounded.RootElement, options.JsonLimits, resolved: false, cancellationToken);
        EndpointDefinitionSemantics.ValidateAuthoredAddressShapes(bounded.RootElement,
            new EndpointDefinitionEvaluation(options, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Resolves Endpoint protocol options using explicit, case-sensitive variable bindings.</summary>
    /// <remarks>
    /// Bindings are JSON string values; null or absent bindings are undefined and fail when referenced.
    /// Non-string JSON is not injected. No references, credentials, or network endpoints are acquired.
    /// </remarks>
    public static EndpointDefinition Materialize(RegistryJson authored, RegistryJson? arguments = null,
        EndpointTemplateOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authored);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new EndpointTemplateOptions();
        options.Validate();
        var owned = RegistryJson.FromElement(authored.RootElement, options.JsonLimits);
        cancellationToken.ThrowIfCancellationRequested();
        var bindings = arguments is null ? null : RegistryJson.FromElement(arguments.RootElement, options.JsonLimits);
        cancellationToken.ThrowIfCancellationRequested();
        var groups = options.MessageGroups is null ? null : RegistryJson.FromElement(options.MessageGroups.RootElement, options.JsonLimits);
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = EndpointTemplateExpansion.Resolve(owned, bindings, options, cancellationToken);
        EndpointDefinitionSemantics.Validate(resolved.RootElement, options.JsonLimits, cancellationToken);
        var evaluation = new EndpointDefinitionEvaluation(options, cancellationToken);
        var subscriptionFilter = EndpointDefinitionSemantics.ValidateConsumer(resolved.RootElement, evaluation);
        EndpointDefinitionMessageSet.Collect(resolved.RootElement, groups, evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        var definition = new EndpointDefinition(owned, resolved, subscriptionFilter, evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        return definition;
    }
}
