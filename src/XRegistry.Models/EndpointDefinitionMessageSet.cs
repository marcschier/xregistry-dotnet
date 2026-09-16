// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using static XRegistry.Models.EndpointTemplateExpansion;

namespace XRegistry.Models;

internal static class EndpointDefinitionMessageSet
{
    private static readonly Lazy<RegistryGroupDefinition> EndpointGroup =
        new(static () => BuiltInRegistryModels.Compile(RegistryModelKind.Endpoint).Groups["endpoints"]);
    private static readonly Lazy<RegistryGroupDefinition> MessageGroup =
        new(static () => BuiltInRegistryModels.Compile(RegistryModelKind.Message).Groups["messagegroups"]);

    internal static void Collect(JsonElement endpoint, RegistryJson? supplied, EndpointDefinitionEvaluation evaluation)
    {
        var groups = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (supplied is not null)
        {
            RequireObject(supplied.RootElement, "/suppliedmessagegroups");
            foreach (var group in supplied.RootElement.EnumerateObject())
            {
                evaluation.CancellationToken.ThrowIfCancellationRequested();
                if (groups.Count == evaluation.Options.MaxMessageGroups)
                {
                    throw Error("endpoint_message_limit", "/suppliedmessagegroups", "The supplied Groups exceed their count budget.");
                }
                groups.Add(group.Name, group.Value);
            }
        }
        var contract = Selectors(endpoint);
        CollectMessages(endpoint, "", "", contract, null, evaluation, inline: true);
        if (endpoint.TryGetProperty("messagegroups", out var references))
        {
            if (references.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("/messagegroups", "messagegroups must be an array of Group URI references.");
            }
            if (references.GetArrayLength() > evaluation.Options.MaxMessageGroups)
            {
                throw Error("endpoint_message_limit", "/messagegroups", "The declared Group references exceed their count budget.");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var reference in references.EnumerateArray())
            {
                evaluation.CancellationToken.ThrowIfCancellationRequested();
                var path = At("/messagegroups", (index++).ToString(CultureInfo.InvariantCulture));
                if (reference.ValueKind != JsonValueKind.String)
                {
                    throw Invalid(path, "A Message Group reference must be a URI string.");
                }
                var value = reference.GetString()!;
                ValidateGroupReference(value, path);
                if (!seen.Add(value))
                {
                    continue;
                }
                if (!groups.TryGetValue(value, out var group))
                {
                    evaluation.UnresolvedMessageGroups.Add(value);
                    evaluation.Defer("message_group", path, "The referenced Group was not supplied; no acquisition was attempted.");
                    continue;
                }
                var groupPath = At("/suppliedmessagegroups", value);
                RequireObject(group, groupPath);
                ValidateWithPath(() => RegistryDomainRules.ValidateMessageGroupMetadata(group, evaluation.CancellationToken), groupPath);
                CollectMessages(group, value, groupPath, contract, Selectors(group), evaluation, inline: false);
            }
        }
        if (evaluation.Messages.Count != 0)
        {
            evaluation.Defer("message_definition", "/messages",
                "Selected definitions still require Message binding, schema, and runtime-payload evaluation by the consumer.");
        }
    }

    private static void CollectMessages(JsonElement group, string reference, string path, RegistryJson endpointContract,
        RegistryJson? sourceContract, EndpointDefinitionEvaluation evaluation, bool inline)
    {
        if (!group.TryGetProperty("messages", out var messages))
        {
            if (!inline || group.TryGetProperty("messagesurl", out _) || group.TryGetProperty("messagescount", out _))
            {
                evaluation.UnresolvedMessageGroups.Add(reference);
                evaluation.Defer("message_group", At(path, "messages"),
                    "The Group's messages collection was not supplied; omission is not evidence that it is empty.");
            }
            return;
        }
        var messagesPath = At(path, "messages");
        RequireObject(messages, messagesPath);
        foreach (var message in messages.EnumerateObject())
        {
            evaluation.CancellationToken.ThrowIfCancellationRequested();
            var messagePath = At(messagesPath, message.Name);
            RequireObject(message.Value, messagePath);
            ValidateWithPath(() => RegistryId.Parse(message.Name), messagePath);
            if (message.Value.TryGetProperty("messageid", out var id) &&
                (id.ValueKind != JsonValueKind.String || id.GetString() != message.Name))
            {
                throw Invalid(At(messagePath, "messageid"), "The Message ID must agree with its collection key.");
            }
            var deferred = NeedsMaterialization(message.Value);
            var omitEnvelope = deferred && !message.Value.TryGetProperty("envelope", out _);
            if (sourceContract is not null)
            {
                ValidateConstraints(message.Value, MessageGroup.Value, sourceContract, omitEnvelope, messagePath, evaluation);
            }
            var effectiveSelectors = Selectors(message.Value, fallback: sourceContract?.RootElement);
            ValidateConstraints(effectiveSelectors.RootElement, EndpointGroup.Value, endpointContract, omitEnvelope, messagePath, evaluation);
            if (deferred)
            {
                evaluation.Defer("message_materialization", messagePath,
                    "Message inheritance or borrowing must be resolved explicitly before evaluating its final group and runtime contracts.");
            }
            evaluation.AddMessage(message.Name, reference, message.Value, messagePath);
        }
        if (group.TryGetProperty("messagescount", out var count))
        {
            if (count.ValueKind != JsonValueKind.Number)
            {
                throw Invalid(At(path, "messagescount"), "The collection count must be a nonnegative integer.");
            }
            var number = RegistryNumber.FromElement(count, evaluation.Options.JsonLimits);
            if (!number.IsInteger || number.Significand.Sign < 0)
            {
                throw Invalid(At(path, "messagescount"), "The collection count must be a nonnegative integer.");
            }
            var expected = RegistryNumber.Parse(messages.EnumerateObject().Count().ToString(CultureInfo.InvariantCulture));
            if (!number.Equals(expected))
            {
                evaluation.UnresolvedMessageGroups.Add(reference);
                evaluation.Defer("message_group", messagesPath, "The supplied collection does not match its count and may be incomplete.");
            }
        }
    }

    private static bool NeedsMaterialization(JsonElement message) =>
        HasValue(message, "basemessage") || HasValue(message, "xref") ||
        message.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object && HasValue(meta, "xref");

    private static bool HasValue(JsonElement value, string name) =>
        value.TryGetProperty(name, out var member) && member.ValueKind != JsonValueKind.Null;

    private static RegistryJson Selectors(JsonElement value, bool omitEnvelope = false, JsonElement? fallback = null) =>
        RegistryJson.Create(writer =>
        {
            writer.WriteStartObject();
            if (value.TryGetProperty("protocol", out var protocol) ||
                fallback is { } source && source.TryGetProperty("protocol", out protocol))
            {
                writer.WritePropertyName("protocol");
                protocol.WriteTo(writer);
            }
            if (!omitEnvelope && value.TryGetProperty("envelope", out var envelope))
            {
                writer.WritePropertyName("envelope");
                envelope.WriteTo(writer);
            }
            writer.WriteEndObject();
        });

    private static void ValidateConstraints(JsonElement message, RegistryGroupDefinition definition, RegistryJson contract,
        bool omitEnvelope, string path, EndpointDefinitionEvaluation evaluation)
    {
        var effective = omitEnvelope ? Selectors(contract.RootElement, omitEnvelope: true) : contract;
        ValidateWithPath(() => RegistryDomainRules.ValidateMessageGroupConstraints(message, definition,
            definition.Resources["messages"], effective.RootElement, evaluation.CancellationToken), path);
    }

    private static void ValidateGroupReference(string value, string path)
    {
        var absolute = EndpointDefinitionSemantics.ValidateReference(value, path);
        if (value.StartsWith('/'))
        {
            RegistryPath parsed;
            try
            {
                parsed = RegistryPath.Parse(value);
            }
            catch (RegistryException exception)
            {
                throw new RegistryException(new("invalid_attribute", path, "A same-registry Message Group reference must be a Group XID."), exception);
            }
            if (parsed.Kind != RegistryPathKind.Group || parsed.GroupType != "messagegroups")
            {
                throw Invalid(path, "The XID must identify a messagegroups Group, not a Resource or collection.");
            }
        }
        else if (!absolute)
        {
            throw Invalid(path, "A reference must be a same-registry Group XID or an absolute external Group URI.");
        }
        else if (EndpointDefinitionSemantics.ReferenceHasUserInfo(value))
        {
            throw Error("endpoint_credentials", path, "Group references must not embed credentials.");
        }
    }

    private static void RequireObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(path, "A Group or Message collection entry must be an object.");
        }
    }

    private static void ValidateWithPath(Action validate, string path)
    {
        try
        {
            validate();
        }
        catch (RegistryException exception)
        {
            throw new RegistryException(exception.Diagnostic with { Path = path + exception.Diagnostic.Path }, exception);
        }
    }

    private static RegistryException Invalid(string path, string message) => Error("invalid_attribute", path, message);
}
