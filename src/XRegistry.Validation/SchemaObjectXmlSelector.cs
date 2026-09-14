using System.Xml;
using System.Xml.Linq;

namespace XRegistry.Validation;

internal sealed class SchemaObjectXmlSelector(ValidationContext context, SchemaObjectSelectionOptions options)
{
    private const string SchemaNamespace = "http://www.w3.org/2001/XMLSchema";

    internal async ValueTask<(XElement Node, SchemaTypeKind Kind, string Name)> SelectAsync(
        ReadOnlyMemory<byte> bytes, string expression)
    {
        var (descendants, steps) = Parse(expression);
        var syntax = new XmlSchemaSyntax(context);
        await syntax.ValidateAsync(bytes).ConfigureAwait(false);
        context.Work(bytes.Length);
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = XmlReader.Create(stream, syntax.Settings());
        var document = XDocument.Load(reader);
        context.CancellationToken.ThrowIfCancellationRequested();
        var root = document.Root!;
        var matches = steps.Count == 0 ? DefaultDeclarations(root) : Evaluate(root, descendants, steps);
        if (matches.Count == 0)
        {
            SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.NotFound, "selection.not_found",
                "The structural selector did not match a declaration.");
        }
        if (matches.Count > 1)
        {
            SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Ambiguous, "selection.ambiguous",
                "More than one XML node matches; select a single declaration explicitly.");
        }

        var selected = matches[0];
        var isElement = selected.Name == XName.Get("element", SchemaNamespace);
        var isType = selected.Name.NamespaceName == SchemaNamespace &&
            selected.Name.LocalName is "simpleType" or "complexType";
        if ((!isElement && !isType) || selected.Attribute("name") is not { Value.Length: > 0 })
        {
            SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.not_type",
                "The selected XML node is not a named type or element declaration.");
        }
        if (selected.Parent != root)
        {
            SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Unsupported, "selection.xsd_scope",
                "This bounded policy selects only global XML Schema type and element declarations.");
        }
        var name = XName.Get(selected.Attribute("name")!.Value, root.Attribute("targetNamespace")?.Value ?? "").ToString();
        return (CopyWithNamespaces(selected),
            isElement ? SchemaTypeKind.XmlSchemaElement : SchemaTypeKind.XmlSchemaType, name);
    }

    private (bool Descendants, List<Step> Steps) Parse(string expression)
    {
        var steps = new List<Step>();
        var namespaces = Namespaces();
        if (expression.Length == 0)
        {
            return (false, steps);
        }
        context.Work(expression.Length, "$/selector");
        if (!expression.StartsWith('/'))
        {
            Unsupported();
        }
        var descendants = expression.StartsWith("//", StringComparison.Ordinal);
        var position = descendants ? 2 : 1;
        while (position < expression.Length)
        {
            if (steps.Count >= options.MaxSelectorSegments)
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/selector", "limit.selector_steps",
                    "The structural XPath exceeds its step limit.");
            }
            WhiteSpace();
            var start = position;
            while (position < expression.Length && !char.IsWhiteSpace(expression[position]) && expression[position] is not ('/' or '['))
            {
                position++;
            }
            var token = expression[start..position];
            if (token.Length == 0)
            {
                Invalid("A structural XPath step is missing.");
            }
            var (ns, local) = QName(token, namespaces);
            WhiteSpace();
            string? name = null;
            if (At('['))
            {
                position++;
                WhiteSpace();
                if (!expression.AsSpan(position).StartsWith("@name", StringComparison.Ordinal))
                {
                    Unsupported();
                }
                position += 5;
                WhiteSpace();
                if (!At('=')) { Unsupported(); }
                position++;
                WhiteSpace();
                if (position >= expression.Length || expression[position] is not ('\'' or '"'))
                {
                    Invalid("An @name predicate requires one quoted XML name.");
                }
                var quote = expression[position++];
                start = position;
                while (position < expression.Length && expression[position] != quote) { position++; }
                if (position == expression.Length) { Invalid("The @name literal is unterminated."); }
                name = expression[start..position++];
                if (!IsName(name)) { Invalid("The @name literal must be an XML NCName."); }
                WhiteSpace();
                if (!At(']')) { Unsupported(); }
                position++;
                WhiteSpace();
            }
            steps.Add(new(ns, local, name));
            if (position == expression.Length) { break; }
            if (!At('/')) { Unsupported(); }
            position++;
            if (At('/')) { Unsupported(); }
            if (position == expression.Length) { Invalid("The structural XPath ends with an empty step."); }
        }
        if (steps.Count == 0) { Invalid("The structural XPath requires a declaration path."); }
        return (descendants, steps);

        bool At(char character) => position < expression.Length && expression[position] == character;
        void WhiteSpace()
        {
            while (position < expression.Length && char.IsWhiteSpace(expression[position])) { position++; }
        }
    }

    private Dictionary<string, string> Namespaces()
    {
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["xs"] = SchemaNamespace,
            ["xsd"] = SchemaNamespace,
            ["xml"] = XNamespace.Xml.NamespaceName,
        };
        if (options.XmlNamespaces is not { } supplied) { return namespaces; }
        if (supplied.Count > 32)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/selector", "limit.namespaces",
                "At most 32 explicit XPath namespace bindings are supported.");
        }
        foreach (var (prefix, uri) in supplied)
        {
            if (prefix is null || uri is null)
            {
                SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.namespace",
                    "XPath prefixes and namespace names cannot be null.");
            }
            if (prefix.Length > options.MaxSelectorLength || uri.Length > options.MaxUriLength)
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/selector", "limit.namespace",
                    "A namespace binding exceeds its character limit.");
            }
            context.Work(prefix.Length + uri.Length, "$/selector");
            if (!IsName(prefix) || prefix == "xmlns" || uri.Length == 0 ||
                prefix == "xml" && uri != XNamespace.Xml.NamespaceName)
            {
                SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.namespace",
                    "XPath prefixes require nonempty namespace bindings; reserved XML prefixes cannot be rebound.");
            }
            namespaces[prefix] = uri;
        }
        return namespaces;
    }

    private static (string? Namespace, string? LocalName) QName(string token, Dictionary<string, string> namespaces)
    {
        if (token == "*") { return (null, null); }
        var colon = token.IndexOf(':');
        if (colon < 0)
        {
            if (!IsName(token)) { Unsupported(); }
            return ("", token);
        }
        var prefix = token[..colon];
        var local = token[(colon + 1)..];
        if (!IsName(prefix) || local != "*" && !IsName(local)) { Unsupported(); }
        if (!namespaces.TryGetValue(prefix, out var ns))
        {
            SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.namespace",
                $"No explicit XPath namespace binding exists for '{prefix}'.");
        }
        return (ns, local == "*" ? null : local);
    }

    private List<XElement> DefaultDeclarations(XElement root)
    {
        var elements = new List<XElement>();
        var types = new List<XElement>();
        foreach (var element in root.Elements())
        {
            context.Work();
            if (element.Name.NamespaceName != SchemaNamespace) { continue; }
            if (element.Name.LocalName == "element") { elements.Add(element); }
            else if (element.Name.LocalName is "simpleType" or "complexType") { types.Add(element); }
        }
        return elements.Count == 0 ? types : elements;
    }

    private List<XElement> Evaluate(XElement root, bool descendants, List<Step> steps)
    {
        IEnumerable<XElement> candidates = descendants ? root.DescendantsAndSelf() : [root];
        List<XElement> matches = [];
        foreach (var step in steps)
        {
            matches = [];
            foreach (var candidate in candidates)
            {
                context.Work(1, "$/selector");
                if ((step.Namespace is null || candidate.Name.NamespaceName == step.Namespace) &&
                    (step.LocalName is null || candidate.Name.LocalName == step.LocalName) &&
                    (step.Name is null || candidate.Attribute("name")?.Value == step.Name))
                {
                    matches.Add(candidate);
                }
            }
            candidates = matches.SelectMany(e => e.Elements());
        }
        return matches;
    }

    private XElement CopyWithNamespaces(XElement selected)
    {
        foreach (var node in selected.DescendantNodesAndSelf())
        {
            context.Work();
        }
        var copy = new XElement(selected);
        foreach (var ancestor in selected.AncestorsAndSelf())
        {
            foreach (var attribute in ancestor.Attributes())
            {
                context.Work();
                if (attribute.IsNamespaceDeclaration && copy.Attribute(attribute.Name) is null)
                {
                    context.Work(attribute.Value.Length);
                    copy.Add(new XAttribute(attribute));
                }
            }
        }
        return copy;
    }

    private static bool IsName(string name)
    {
        try
        {
            XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static void Invalid(string detail)
        => SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.xpath", detail);

    private static void Unsupported()
        => SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Unsupported, "selection.xpath_unsupported",
            "Supported XPath uses child steps, one optional leading '//', namespace-qualified names or '*', and exact [@name='Name'] predicates only.");

    private sealed record Step(string? Namespace, string? LocalName, string? Name);
}
