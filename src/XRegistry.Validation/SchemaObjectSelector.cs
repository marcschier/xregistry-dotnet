// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Validation;

/// <summary>Selects concrete types from already acquired schema Documents without acquiring their URI.</summary>
public static class SchemaObjectSelector
{
    /// <summary>
    /// Validates a supplied Document under the bounded built-in format policy and
    /// selects exactly one concrete declaration. Cancellation and resolver exceptions
    /// propagate; invalid caller options throw argument exceptions.
    /// </summary>
    public static async ValueTask<SchemaObjectSelectionResult> SelectAsync(
        string format,
        ReadOnlyMemory<byte> document,
        string schemaUri,
        SchemaObjectSelectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(schemaUri);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        options.Validate();
        var jsonSchema = JsonSchemaSyntax.IsFormat(format);
        var jsonStructure = format.Equals("JsonStructure", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("JsonStructure/draft-04", StringComparison.OrdinalIgnoreCase);
        var avro = AvroSyntax.IsFormat(format);
        var protobuf = format.Equals("Protobuf/2", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("Protobuf/3", StringComparison.OrdinalIgnoreCase);
        var xsd = format.Equals("XSD/1.0", StringComparison.OrdinalIgnoreCase);
        if (!jsonSchema && !jsonStructure && !avro && !protobuf && !xsd)
        {
            return new(SchemaObjectSelectionStatus.Unsupported,
                new("$", "format.unsupported", $"The format '{format}' is not supported."));
        }

        try
        {
            var documents = new SchemaObjectContext(options.Validation, cancellationToken);
            var context = documents.Validation;
            var reference = SchemaObjectReference.Parse(schemaUri, options, avro || protobuf, context);
            if (protobuf && reference.Selector.Length == 0)
            {
                SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.name_required",
                    "A Protobuf schema URI must select a message declaration explicitly.");
            }
            var owned = documents.OwnRoot(document, reference.DocumentReference);
            SchemaObject? selection = null;
            if (jsonSchema)
            {
                await new JsonSchemaSyntax(format, context).ValidateAsync(owned, SelectRoot).ConfigureAwait(false);
            }
            else if (jsonStructure)
            {
                new JsonStructureSyntax(context).Validate(owned, SelectRoot);
            }
            else if (avro)
            {
                var parser = new AvroSyntax(format, context);
                parser.Parse(owned, (root, rootType) =>
                {
                    var target = rootType;
                    if (reference.Selector.Length != 0)
                    {
                        if (AvroSyntax.Primitive(reference.Selector))
                        {
                            SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.not_type",
                                "An Avro primitive is not a record declaration.");
                        }
                        var name = SchemaTypeNames.Select(reference.Selector,
                            parser.NamedTypes.Select(p => (p.Key, p.Value.Kind == "record")), false, context);
                        target = parser.NamedTypes[name];
                    }
                    if (target.Kind != "record")
                    {
                        SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.not_type",
                            "The Avro Document root is not a record; select a declared record explicitly.");
                    }
                    var node = SchemaJson.DecodedPointer(root, target.DeclarationPath[1..], "$/selector", context);
                    selection = new(format, schemaUri, reference.DocumentReference, owned, options.Validation.DocumentUri,
                        SchemaTypeKind.AvroRecord, target.Name, target.DeclarationPath[1..], node.Value, documents.References);
                });
            }
            else if (protobuf)
            {
                var parser = new ProtobufSyntax(context);
                await parser.ValidateAsync(owned, format[^1] - '0', reference.DocumentReference).ConfigureAwait(false);
                var name = SchemaTypeNames.Select(reference.Selector, parser.RootDeclarations, true, context);
                selection = new(format, schemaUri, reference.DocumentReference, owned, options.Validation.DocumentUri,
                    SchemaTypeKind.ProtobufMessage, name, null, null, documents.References);
            }
            else
            {
                var target = await new SchemaObjectXmlSelector(context, options).SelectAsync(owned, reference.Selector).ConfigureAwait(false);
                selection = new(format, schemaUri, reference.DocumentReference, owned, options.Validation.DocumentUri,
                    target.Kind, target.Name, null, null, documents.References,
                    reference.Selector.Length == 0 ? null : reference.Selector, target.Node);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(selection ?? throw new InvalidOperationException("The schema parser did not return a validated Document."));

            void SelectRoot(JsonElement root, IReadOnlySet<string> paths)
            {
                var selector = reference.Selector;
                if (jsonStructure && selector.Length == 0 && root.TryGetProperty("$root", out var rootSelector))
                {
                    selector = rootSelector.GetString()!;
                }
                var target = SelectJson(root, paths, selector, options, context);
                selection = new(format, schemaUri, reference.DocumentReference, owned,
                    options.Validation.DocumentUri, jsonSchema ? SchemaTypeKind.JsonSchema : SchemaTypeKind.JsonStructure, null, target.Path[1..],
                    target.Value, documents.References);
            }
        }
        catch (SchemaObjectSelectionFailure failure)
        {
            return new(failure.Status, failure.Diagnostic);
        }
        catch (ValidationFailure failure)
        {
            return new(failure.Status switch
            {
                DocumentValidationStatus.Invalid => SchemaObjectSelectionStatus.Invalid,
                DocumentValidationStatus.Unsupported => SchemaObjectSelectionStatus.Unsupported,
                _ => SchemaObjectSelectionStatus.Indeterminate,
            }, failure.Diagnostic);
        }
    }

    private static (JsonElement Value, string Path) SelectJson(JsonElement root, IReadOnlySet<string> schemaPaths,
        string pointer, SchemaObjectSelectionOptions options, ValidationContext context)
    {
        if (pointer.Length > options.MaxSelectorLength)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/selector", "limit.selector",
                "The JSON Pointer exceeds its character limit.");
        }
        if (pointer.Count(c => c == '/') > options.MaxSelectorSegments)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/selector", "limit.selector_steps",
                "The JSON Pointer exceeds its step limit.");
        }
        (JsonElement Value, string Path) target;
        try
        {
            target = SchemaJson.DecodedPointer(root, pointer, "$/selector", context);
        }
        catch (ValidationFailure failure) when (failure.Diagnostic.Code is "reference.pointer" or "reference.not_found")
        {
            var missing = failure.Diagnostic.Code == "reference.not_found";
            throw new SchemaObjectSelectionFailure(missing ? SchemaObjectSelectionStatus.NotFound : SchemaObjectSelectionStatus.Invalid,
                new("$/selector", missing ? "selection.not_found" : "selection.pointer", failure.Diagnostic.Detail));
        }
        if (target.Value.ValueKind != JsonValueKind.Object || !schemaPaths.Contains(target.Path))
        {
            SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.not_type",
                "The selected value is not an object in a declared schema position.");
        }
        return target;
    }
}
