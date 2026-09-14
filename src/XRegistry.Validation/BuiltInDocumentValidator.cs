namespace XRegistry.Validation;

/// <summary>Managed, explicitly bounded schema-document validation.</summary>
public sealed class BuiltInDocumentValidator : IDocumentValidator
{
    /// <inheritdoc />
    public async ValueTask<DocumentValidationResult> ValidateAsync(
        string format,
        ReadOnlyMemory<byte> document,
        DocumentValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        options.Validate();
        try
        {
            var context = new ValidationContext(options, cancellationToken);
            if (JsonSchemaSyntax.IsFormat(format))
            {
                await new JsonSchemaSyntax(format, context).ValidateAsync(document).ConfigureAwait(false);
            }
            else if (format.Equals("XSD/1.0", StringComparison.OrdinalIgnoreCase))
            {
                await new XmlSchemaSyntax(context).ValidateAsync(document).ConfigureAwait(false);
            }
            else if (AvroSyntax.IsFormat(format))
            {
                new AvroSyntax(format, context).Parse(document);
            }
            else if (format.Equals("Protobuf/2", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("Protobuf/3", StringComparison.OrdinalIgnoreCase))
            {
                await new ProtobufSyntax(context).ValidateAsync(document, format[^1] - '0').ConfigureAwait(false);
            }
            else if (format.Equals("JsonStructure", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("JsonStructure/draft-04", StringComparison.OrdinalIgnoreCase))
            {
                new JsonStructureSyntax(context).Validate(document);
            }
            else
            {
                return new DocumentValidationResult(DocumentValidationStatus.Unsupported,
                    [new("$", "format.unsupported", $"The format '{format}' is not supported.")]);
            }

            return new DocumentValidationResult(DocumentValidationStatus.Valid);
        }
        catch (ValidationFailure failure)
        {
            return new DocumentValidationResult(failure.Status, [failure.Diagnostic]);
        }
    }
}
