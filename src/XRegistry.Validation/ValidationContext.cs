using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace XRegistry.Validation;

internal sealed class ValidationContext(DocumentValidationOptions options, CancellationToken cancellationToken)
{
    private long _bytes;
    private long _work;
    private long _nodes;
    private int _references;

    internal DocumentValidationOptions Options { get; } = options;
    internal CancellationToken CancellationToken { get; } = cancellationToken;

    internal void Bytes(int count, string path)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (count > Options.MaxDocumentBytes)
        {
            Fail(DocumentValidationStatus.Indeterminate, path, "limit.bytes", "The per-document byte limit was exceeded.");
        }

        _bytes += count;
        if (_bytes > Options.MaxTotalBytes)
        {
            Fail(DocumentValidationStatus.Indeterminate, path, "limit.total_bytes", "The total document byte limit was exceeded.");
        }

        Work(count, path);
    }

    internal void Work(long count = 1, string path = "$")
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (count > Options.MaxWork - _work)
        {
            Fail(DocumentValidationStatus.Indeterminate, path, "limit.work", "The validation work limit was exceeded.");
        }

        _work += count;
    }

    internal void Node(int depth, string path = "$")
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (depth > Options.MaxDepth)
        {
            Fail(DocumentValidationStatus.Indeterminate, path, "limit.depth", "The document nesting limit was exceeded.");
        }

        if (++_nodes > Options.MaxNodes)
        {
            Fail(DocumentValidationStatus.Indeterminate, path, "limit.nodes", "The parsed node limit was exceeded.");
        }
    }

    internal async ValueTask<ReadOnlyMemory<byte>> ResolveAsync(string reference, string path)
    {
        Work(1, path);
        if (++_references > Options.MaxReferences)
        {
            Fail(DocumentValidationStatus.Indeterminate, path, "limit.references", "The external reference limit was exceeded.");
        }

        var resolver = Options.ResolveReference;
        ReadOnlyMemory<byte>? bytes = resolver is null
            ? null
            : await resolver(reference, CancellationToken).ConfigureAwait(false);
        CancellationToken.ThrowIfCancellationRequested();
        if (bytes is null)
        {
            Fail(DocumentValidationStatus.Indeterminate, path, "reference.unresolved", $"No explicitly supplied schema resolves '{reference}'.");
        }

        return bytes.Value;
    }

    internal JsonDocument ReadJson(ReadOnlyMemory<byte> bytes, string path = "$")
    {
        Bytes(bytes.Length, path);
        try
        {
            var document = RegistryJson.ParseDocument(bytes.Span, new RegistryJsonLimits
            {
                MaxBytes = Options.MaxDocumentBytes,
                MaxDepth = Options.MaxDepth,
                MaxNodes = Options.MaxNodes
            });
            try
            {
                CountNodes(document.RootElement, 1, path);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (RegistryException error)
        {
            var code = error.Diagnostic.Code switch
            {
                "duplicate_member" => "json.duplicate_property",
                "depth_limit" => "limit.depth",
                "node_limit" => "limit.nodes",
                "byte_limit" => "limit.bytes",
                _ => error.Diagnostic.Code.EndsWith("_limit", StringComparison.Ordinal) ? "limit.number" : "json.syntax"
            };
            throw new ValidationFailure(code.StartsWith("limit.", StringComparison.Ordinal) ?
                DocumentValidationStatus.Indeterminate : DocumentValidationStatus.Invalid,
                new(path + error.Diagnostic.Path, code, error.Message));
        }
    }

    private void CountNodes(JsonElement value, int depth, string path)
    {
        Node(depth, path);
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                CountNodes(property.Value, depth + 1, SchemaJson.Path(path, property.Name));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                CountNodes(item, depth + 1, SchemaJson.Path(path, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
    }
    internal static void Require([DoesNotReturnIf(false)] bool condition, string path, string code, string detail)
    {
        if (!condition)
        {
            Fail(DocumentValidationStatus.Invalid, path, code, detail);
        }
    }

    [DoesNotReturn]
    internal static void Fail(DocumentValidationStatus status, string path, string code, string detail)
        => throw new ValidationFailure(status, new(path, code, detail));
}

internal sealed class ValidationFailure(DocumentValidationStatus status, DocumentDiagnostic diagnostic) : Exception(diagnostic.Detail)
{
    internal DocumentValidationStatus Status { get; } = status;
    internal DocumentDiagnostic Diagnostic { get; } = diagnostic;
}
