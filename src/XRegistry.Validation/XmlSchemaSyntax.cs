// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace XRegistry.Validation;

internal sealed class XmlSchemaSyntax(ValidationContext context)
{
    private const string Namespace = "http://www.w3.org/2001/XMLSchema";
    private readonly Dictionary<(string Reference, bool Catalog), XmlSchema> _schemas = [];
    private readonly Dictionary<XmlSchema, List<XmlSchema>> _dependencies = [];
    private long _elements;
    private long _occurrenceCost = 1;

    internal async ValueTask ValidateAsync(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var schema = await LoadAsync(bytes, context.Options.DocumentUri?.AbsoluteUri ?? "", 1).ConfigureAwait(false);
            CheckDependencyDepth(schema, [], 1);
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

    internal void ValidateEmbedded(IReadOnlyList<XElement> roots,
        IReadOnlyDictionary<XElement, XElement[]>? dependencies = null)
    {
        try
        {
            var schemas = new XmlSchemaSet { XmlResolver = null };
            schemas.ValidationEventHandler += OnValidation;
            var parsed = new Dictionary<XElement, XmlSchema>();
            foreach (var root in roots)
            {
                var bytes = Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
                context.Work(bytes.Length);
                Preflight(bytes, prepaid: true);
                using var stream = new MemoryStream(bytes, false);
                using var reader = XmlReader.Create(stream, Settings(embedded: true));
                var schema = XmlSchema.Read(reader, OnValidation);
                ValidationContext.Require(schema is not null, "$", "xsd.schema", "An XML Schema document is required.");
                if (schema.Includes.Count > 0 && dependencies is null)
                {
                    ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$", "xsd.embedded_dependency",
                        "This embedded-schema policy does not resolve schema imports or includes.");
                }
                parsed.Add(root, schema);
            }
            context.Work(CompilationWork());
            foreach (var (root, schema) in parsed)
            {
                if (schema.Includes.Count == 0) { continue; }
                XElement[]? targets = null;
                ValidationContext.Require(dependencies is not null && dependencies.TryGetValue(root, out targets) &&
                    targets.Length == schema.Includes.Count, "$", "xsd.embedded_dependency",
                    "Each embedded schema dependency requires an explicitly resolved schema.");
                for (var index = 0; index < schema.Includes.Count; index++)
                {
                    ((XmlSchemaExternal)schema.Includes[index]).Schema = parsed[targets[index]];
                }
            }
            foreach (var schema in parsed.Values) { schemas.Add(schema); }
            context.CancellationToken.ThrowIfCancellationRequested();
            schemas.Compile();
            context.CancellationToken.ThrowIfCancellationRequested();
            ValidationContext.Require(schemas.IsCompiled, "$", "xsd.compilation", "The embedded XML Schemas did not compile.");
        }
        catch (XmlException error)
        {
            throw new ValidationFailure(DocumentValidationStatus.Invalid,
                new(LinePath(error.LineNumber, error.LinePosition), "xml.syntax",
                    "The embedded schema is not permitted well-formed XML (DTDs are prohibited)."));
        }
        catch (XmlSchemaException error)
        {
            throw new ValidationFailure(DocumentValidationStatus.Invalid,
                new(LinePath(error.LineNumber, error.LinePosition), "xsd.schema", error.Message));
        }
    }

    private async ValueTask<XmlSchema> LoadAsync(ReadOnlyMemory<byte> bytes, string uri, int depth, bool catalogOrigin = false)
    {
        context.Node(depth);
        context.Bytes(bytes.Length, "$");
        Preflight(bytes);
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = XmlReader.Create(stream, Settings());
        context.Work(bytes.Length);
        var root = XDocument.Load(reader, LoadOptions.SetLineInfo).Root!;
        using var schemaReader = root.CreateReader();
        var schema = XmlSchema.Read(schemaReader, OnValidation);
        ValidationContext.Require(schema is not null, "$", "xsd.schema", "An XML Schema document is required.");
        _schemas.Add((uri, catalogOrigin), schema);
        var targets = new List<XmlSchema>();
        _dependencies.Add(schema, targets);
        var declarations = root.Elements().Where(element => element.Name == XName.Get("import", Namespace) ||
            element.Name == XName.Get("include", Namespace)).ToArray();
        ValidationContext.Require(declarations.Length == schema.Includes.Count, "$", "xsd.schema",
            "Every schema dependency must correspond to its authored declaration.");
        for (var index = 0; index < schema.Includes.Count; index++)
        {
            context.Work();
            var external = (XmlSchemaExternal)schema.Includes[index];
            var reference = external.SchemaLocation;
            var catalog = false;
            if (string.IsNullOrEmpty(reference))
            {
                reference = external is XmlSchemaImport import ? import.Namespace : null;
                catalog = true;
            }
            if (string.IsNullOrEmpty(reference))
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$", "reference.unresolved",
                    "An XML Schema import has no explicit location or namespace registration.");
            }
            var key = catalog ? reference : DependencyUri(declarations[index], reference, catalogOrigin ? "" : uri);
            if (!_schemas.TryGetValue((key, catalog), out var dependency))
            {
                var supplied = await context.ResolveAsync(key, "$").ConfigureAwait(false);
                dependency = await LoadAsync(supplied, key, depth + 1, catalog).ConfigureAwait(false);
            }
            external.Schema = dependency;
            targets.Add(dependency);
        }
        return schema;
    }

    private string DependencyUri(XElement declaration, string reference, string origin)
    {
        if (reference.Contains('#', StringComparison.Ordinal))
        {
            ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$", "xsd.schema_fragment",
                "Standalone XSD validation does not select embedded schemas through location fragments.");
        }
        Uri.TryCreate(origin, UriKind.Absolute, out var baseUri);
        foreach (var element in declaration.AncestorsAndSelf().Reverse())
        {
            if (element.Attribute(XNamespace.Xml + "base") is { } xmlBase)
            {
                if (xmlBase.Value.Length == 0) { continue; }
                var value = Resolve(xmlBase.Value, baseUri);
                if (!Uri.TryCreate(value, UriKind.Absolute, out baseUri))
                {
                    ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$", "dependency.base",
                        "A relative XML base requires an absolute containing schema origin.");
                }
            }
        }
        return Resolve(reference, baseUri);

        string Resolve(string value, Uri? relativeTo)
        {
            context.Work(value.Length + 1L);
            ValidationContext.Require(!value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) &&
                !value.Contains('\\', StringComparison.Ordinal) &&
                !value.Contains('#', StringComparison.Ordinal) &&
                Uri.IsWellFormedUriString(value, UriKind.RelativeOrAbsolute),
                "$", "dependency.uri", "A schema retrieval location must be a well-formed, fragment-free URI reference.");
            if (!value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            {
                return absolute.AbsoluteUri;
            }
            if (relativeTo is null)
            {
                if (value.StartsWith('/'))
                {
                    ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$", "dependency.base",
                        "A rooted schema location requires an absolute containing schema origin.");
                }
                return value;
            }
            ValidationContext.Require(Uri.TryCreate(relativeTo, value, out var resolved), "$", "dependency.uri",
                "The schema dependency URI cannot be resolved against its containing base.");
            return resolved.AbsoluteUri;
        }
    }

    private void CheckDependencyDepth(XmlSchema schema, HashSet<XmlSchema> path, int depth)
    {
        context.Work();
        if (!path.Add(schema)) { return; }
        if (depth > context.Options.MaxDepth)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$", "limit.depth",
                "The completed schema dependency graph exceeds the depth limit.");
        }
        foreach (var dependency in _dependencies[schema])
        {
            CheckDependencyDepth(dependency, path, depth + 1);
        }
        path.Remove(schema);
    }

    private void Preflight(ReadOnlyMemory<byte> bytes, bool prepaid = false)
    {
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = XmlReader.Create(stream, Settings(embedded: prepaid));
        var sawRoot = false;
        while (reader.Read())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (prepaid) { context.Work(); }
            else { context.Node(reader.Depth + 1); }
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
                if (prepaid) { context.Work(); }
                else { context.Node(reader.Depth + 2); }
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

    internal XmlReaderSettings Settings(bool embedded = false) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = embedded ? context.Options.MaxWork : context.Options.MaxDocumentBytes,
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
