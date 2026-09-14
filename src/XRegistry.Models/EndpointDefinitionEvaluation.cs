using System.Text;
using static XRegistry.Models.EndpointTemplateExpansion;

namespace XRegistry.Models;

internal sealed class EndpointDefinitionEvaluation(EndpointTemplateOptions options, CancellationToken cancellationToken)
{
    private int _deferredBytes;
    private int _messageBytes;
    private readonly HashSet<string> _messageIds = new(StringComparer.Ordinal);

    internal EndpointTemplateOptions Options { get; } = options;
    internal CancellationToken CancellationToken { get; } = cancellationToken;
    internal List<RegistryDiagnostic> DeferredChecks { get; } = [];
    internal List<EndpointDefinitionMessage> Messages { get; } = [];
    internal HashSet<string> UnresolvedMessageGroups { get; } = new(StringComparer.Ordinal);

    internal void AddMessage(string id, string groupReference, System.Text.Json.JsonElement metadata, string path)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (Messages.Count == Options.MaxMessages)
        {
            throw Error("endpoint_message_limit", path, "The endpoint exceeds its Message candidate count budget.");
        }
        var bytes = (long)Encoding.UTF8.GetByteCount(metadata.GetRawText()) +
            Encoding.UTF8.GetByteCount(id) + Encoding.UTF8.GetByteCount(groupReference);
        if (bytes > Options.MaxMessageBytes - _messageBytes)
        {
            throw Error("endpoint_message_limit", path, "Message candidates exceed their aggregate UTF-8 byte budget.");
        }
        _messageBytes += (int)bytes;
        if (!_messageIds.Add(id) && Options.RejectDuplicateMessageIds)
        {
            throw Error("duplicate_message_id", path, "The explicit uniqueness policy rejects duplicate Message IDs across collections.");
        }
        Messages.Add(new EndpointDefinitionMessage(id, groupReference, RegistryJson.FromElement(metadata, Options.JsonLimits)));
    }

    internal void Defer(string code, string path, string message)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (DeferredChecks.Count == Options.MaxDeferredChecks)
        {
            throw Error("endpoint_deferred_limit", path, "The endpoint exceeds its deferred-obligation count budget.");
        }
        var bytes = (long)Encoding.UTF8.GetByteCount(code) + Encoding.UTF8.GetByteCount(path) + Encoding.UTF8.GetByteCount(message);
        if (bytes > Options.MaxDeferredBytes - _deferredBytes)
        {
            throw Error("endpoint_deferred_limit", path, "Deferred obligations exceed their aggregate UTF-8 byte budget.");
        }
        _deferredBytes += (int)bytes;
        DeferredChecks.Add(new RegistryDiagnostic(code, path, message));
    }
}
