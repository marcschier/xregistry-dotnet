namespace XRegistry.Validation;

internal sealed class SchemaObjectContext
{
    private readonly DocumentValidationOptions _options;
    private long _ownedBytes;
    private string _rootReference = "";
    private ReadOnlyMemory<byte> _root;

    internal SchemaObjectContext(DocumentValidationOptions options, CancellationToken cancellationToken)
    {
        _options = options;
        Validation = new(options with { ResolveReference = ResolveAsync }, cancellationToken);
    }

    internal ValidationContext Validation { get; }
    internal Dictionary<string, ReadOnlyMemory<byte>> References { get; } = new(StringComparer.Ordinal);

    internal ReadOnlyMemory<byte> OwnRoot(ReadOnlyMemory<byte> document, string reference)
    {
        _rootReference = reference;
        _root = Copy(document);
        return _root;
    }

    private async ValueTask<ReadOnlyMemory<byte>?> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.Equals(_rootReference, StringComparison.Ordinal) ||
            reference.Equals(_options.DocumentUri?.AbsoluteUri, StringComparison.Ordinal))
        {
            return _root;
        }
        if (References.TryGetValue(reference, out var previous))
        {
            return previous;
        }
        var supplied = _options.ResolveReference is { } resolver
            ? await resolver(reference, cancellationToken).ConfigureAwait(false) : null;
        cancellationToken.ThrowIfCancellationRequested();
        if (supplied is null)
        {
            return null;
        }
        var owned = Copy(supplied.Value);
        References.Add(reference, owned);
        return owned;
    }

    private byte[] Copy(ReadOnlyMemory<byte> document)
    {
        if (document.Length > _options.MaxDocumentBytes)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$", "limit.bytes",
                "The per-document byte limit was exceeded.");
        }
        if (document.Length > _options.MaxTotalBytes - _ownedBytes)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$", "limit.total_bytes",
                "The total owned document byte limit was exceeded.");
        }
        Validation.Work(document.Length);
        _ownedBytes += document.Length;
        return document.ToArray();
    }
}
