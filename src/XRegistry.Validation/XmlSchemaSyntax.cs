using System.Globalization;
using System.Xml;
using System.Xml.Schema;

namespace XRegistry.Validation;

internal sealed class XmlSchemaSyntax(ValidationContext context)
{
    private const string Namespace = "http://www.w3.org/2001/XMLSchema";
    private readonly Dictionary<string, XmlSchema> _schemas = new(StringComparer.Ordinal);
    private long _elements;
    private long _occurrenceCost = 1;

    internal async ValueTask ValidateAsync(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var schema = await LoadAsync(bytes, context.Options.DocumentUri?.AbsoluteUri ?? "", 1).ConfigureAwait(false);
            context.Work(CompilationWork());
            var schemas = new XmlSchemaSet { XmlResolver = null };
            schemas.ValidationEventHandler += OnValidation;
            schemas.Add(schema);
            context.CancellationToken.ThrowIfCancellationRequested();
            schemas.Compile();
            context.CancellationToken.ThrowIfCancellationRequested();
            ValidationContext.Require(schemas.IsCompiled, "$", "xsd.compilation", "The XML Schema did not compile.");
        }
        catch (XmlException error)
        {
            throw new ValidationFailure(DocumentValidationStatus.Invalid,
                new(LinePath(error.LineNumber, error.LinePosition), "xml.syntax", "The document is not permitted well-formed XML (DTDs are prohibited)."));
        }
        catch (XmlSchemaException error)
        {
            throw new ValidationFailure(DocumentValidationStatus.Invalid,
                new(LinePath(error.LineNumber, error.LinePosition), "xsd.schema", error.Message));
        }
    }

    private async ValueTask<XmlSchema> LoadAsync(ReadOnlyMemory<byte> bytes, string uri, int depth)
    {
        context.Node(depth);
        context.Bytes(bytes.Length, "$");
        Preflight(bytes);
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = XmlReader.Create(stream, Settings());
        var schema = XmlSchema.Read(reader, OnValidation);
        ValidationContext.Require(schema is not null, "$", "xsd.schema", "An XML Schema document is required.");
        _schemas.Add(uri, schema);
        foreach (XmlSchemaExternal external in schema.Includes)
        {
            context.Work();
            var reference = external.SchemaLocation;
            if (string.IsNullOrEmpty(reference))
            {
                reference = external is XmlSchemaImport import ? import.Namespace : null;
            }
            if (string.IsNullOrEmpty(reference))
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$", "reference.unresolved",
                    "An XML Schema import has no explicit location or namespace registration.");
            }
            var key = Uri.TryCreate(uri, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, reference, out var resolved)
                ? resolved.AbsoluteUri : reference;
            if (!_schemas.TryGetValue(key, out var dependency))
            {
                var supplied = await context.ResolveAsync(key, "$").ConfigureAwait(false);
                dependency = await LoadAsync(supplied, key, depth + 1).ConfigureAwait(false);
            }
            external.Schema = dependency;
        }
        return schema;
    }

    private void Preflight(ReadOnlyMemory<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = XmlReader.Create(stream, Settings());
        var sawRoot = false;
        while (reader.Read())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            context.Node(reader.Depth + 1);
            if (!sawRoot)
            {
                ValidationContext.Require(reader.LocalName == "schema" && reader.NamespaceURI == Namespace,
                    "$", "xsd.root", "The root must be the W3C XML Schema 'schema' element.");
                sawRoot = true;
            }
            _elements++;
            if (reader.NamespaceURI == Namespace)
            {
                if (reader.LocalName is "pattern" or "redefine" or "choice" or "all" or "union"
                    or "complexContent" or "key" or "keyref" or "unique"
                    || reader.LocalName is "group" or "attributeGroup" && reader.GetAttribute("ref") is not null)
                {
                    ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$", "xsd.construct_unsupported",
                        $"The '{reader.LocalName}' construct is outside the bounded XSD compilation policy.");
                }
                var maxOccurs = reader.GetAttribute("maxOccurs");
                if (maxOccurs is not null and not "unbounded"
                    && ulong.TryParse(maxOccurs, NumberStyles.None, CultureInfo.InvariantCulture, out var repetitions))
                {
                    if (repetitions > (ulong)(context.Options.MaxWork / _occurrenceCost))
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$", "limit.work",
                            "XML Schema occurrence expansion exceeds the compilation budget.");
                    }
                    _occurrenceCost *= (long)Math.Max(1, repetitions);
                }
            }
            for (var i = 0; i < reader.AttributeCount; i++)
            {
                context.Node(reader.Depth + 2);
            }
        }
        ValidationContext.Require(sawRoot, "$", "xsd.root", "An XML Schema root is required.");
    }

    private long CompilationWork()
    {
        var remaining = context.Options.MaxWork / _occurrenceCost;
        if (_elements > remaining / Math.Max(1, _elements))
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$", "limit.work",
                "XML Schema compilation exceeds the conservative structural budget.");
        }
        return _elements * _elements * _occurrenceCost;
    }

    internal XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = context.Options.MaxDocumentBytes,
        MaxCharactersFromEntities = 1_024,
        IgnoreComments = true,
    };

    private static void OnValidation(object? sender, ValidationEventArgs args)
        => ValidationContext.Fail(args.Severity == XmlSeverityType.Error
            ? DocumentValidationStatus.Invalid : DocumentValidationStatus.Indeterminate,
            LinePath(args.Exception.LineNumber, args.Exception.LinePosition),
            args.Severity == XmlSeverityType.Error ? "xsd.schema" : "xsd.unresolved", args.Message);

    private static string LinePath(int line, int position)
        => string.Create(CultureInfo.InvariantCulture, $"$/line/{line}/column/{position}");
}
