// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Xml;
using System.Xml.Linq;
using System.Collections.ObjectModel;

namespace XRegistry.Validation;

/// <summary>An independently retained global XML Schema element and its acquired schema context.</summary>
public sealed class Wsdl20SchemaElementSelection
{
    internal Wsdl20SchemaElementSelection(string name, string xml, ReadOnlyCollection<string> schemas, Uri? documentUri,
        ReadOnlyCollection<Uri?> schemaDocumentUris)
    {
        ElementName = name;
        ElementXml = xml;
        SchemaDocuments = schemas;
        DocumentUri = documentUri;
        SchemaDocumentUris = schemaDocumentUris;
    }

    /// <summary>Gets the expanded name of the global element, not a type name.</summary>
    public string ElementName { get; }
    /// <summary>Gets retained element declaration XML with its in-scope namespaces.</summary>
    public string ElementXml { get; }
    /// <summary>Gets the independently owned schema documents needed to interpret the declaration.</summary>
    public ReadOnlyCollection<string> SchemaDocuments { get; }
    /// <summary>Gets the declaring schema's origin, or its containing WSDL document's origin for inline schemas.</summary>
    public Uri? DocumentUri { get; }
    /// <summary>Gets each schema document's origin, in the same order as SchemaDocuments; catalog origins remain null.</summary>
    public ReadOnlyCollection<Uri?> SchemaDocumentUris { get; }
}

/// <summary>An independently retained WSDL 2.0 fault reference and its declaring fault component.</summary>
public sealed class Wsdl20FaultReferenceSelection
{
    internal Wsdl20FaultReferenceSelection(string declaringInterface, string faultName, string contentModel,
        string faultXml, string direction, string messageLabel, Wsdl20SchemaElementSelection? schemaElement)
    {
        DeclaringInterfaceName = declaringInterface;
        FaultName = faultName;
        ContentModel = contentModel;
        FaultXml = faultXml;
        Direction = direction;
        MessageLabel = messageLabel;
        SchemaElement = schemaElement;
    }

    /// <summary>Gets the expanded name of the interface that declares the fault.</summary>
    public string DeclaringInterfaceName { get; }
    /// <summary>Gets the expanded name of the referenced fault component.</summary>
    public string FaultName { get; }
    /// <summary>Gets the native fault content model, #any, #none or an expanded element QName.</summary>
    public string ContentModel { get; }
    /// <summary>Gets retained fault declaration XML with its in-scope namespaces.</summary>
    public string FaultXml { get; }
    /// <summary>Gets the fault direction, in or out, relative to the service.</summary>
    public string Direction { get; }
    /// <summary>Gets the effective label of the placeholder associated with this fault.</summary>
    public string MessageLabel { get; }
    /// <summary>Gets the resolved global schema element, or null for #any and #none content.</summary>
    public Wsdl20SchemaElementSelection? SchemaElement { get; }
}

/// <summary>An independently owned WSDL 2.0 operation without binding or endpoint inference.</summary>
public sealed class Wsdl20OperationSelection
{
    internal Wsdl20OperationSelection(string declaringInterface, string operationName, string pattern, XElement operation,
        IEnumerable<string> styles, IEnumerable<Wsdl20FaultReferenceSelection> faults,
        IEnumerable<Wsdl20SchemaElementSelection> schemaElements)
    {
        DeclaringInterfaceName = declaringInterface;
        OperationName = operationName;
        MessageExchangePattern = pattern;
        OperationXml = operation.ToString(SaveOptions.DisableFormatting);
        Styles = Array.AsReadOnly(styles.Distinct(StringComparer.Ordinal).ToArray());
        Faults = Array.AsReadOnly(faults.ToArray());
        SchemaElements = Array.AsReadOnly(schemaElements.Distinct().ToArray());
    }

    /// <summary>Gets the expanded name of the interface that originally declares this operation.</summary>
    public string DeclaringInterfaceName { get; }
    /// <summary>Gets the expanded operation name, retaining its declaring namespace.</summary>
    public string OperationName { get; }
    /// <summary>Gets the native message-exchange-pattern IRI, including its default when omitted.</summary>
    public string MessageExchangePattern { get; }
    /// <summary>Gets retained native operation XML with its in-scope namespaces.</summary>
    public string OperationXml { get; }
    /// <summary>Gets the effective native operation style IRIs, including the declaring interface's defaults.</summary>
    public ReadOnlyCollection<string> Styles { get; }
    /// <summary>Gets resolved fault references with their native identities, directions and effective labels.</summary>
    public ReadOnlyCollection<Wsdl20FaultReferenceSelection> Faults { get; }
    /// <summary>Gets the resolved global elements used by message and fault references.</summary>
    public ReadOnlyCollection<Wsdl20SchemaElementSelection> SchemaElements { get; }
}

public static partial class ApiOperationDocumentResolver
{
    private const string Wsdl20Namespace = "http://www.w3.org/ns/wsdl";
    private const string Wsdl20PatternPrefix = Wsdl20Namespace + "/";
    private sealed record Wsdl20FaultComponent(string DeclaringInterface, string Name, string ContentModel, string Xml,
        Wsdl20SchemaElementSelection? SchemaElement);

    private static async ValueTask<ApiOperationResolutionResult> ResolveWsdl20Async(string contentType, ReadOnlyMemory<byte> bytes,
        ApiOperationSelector selector, Uri? documentUri, ApiOperationDocumentDependencyResolver? resolver,
        CancellationToken token)
    {
        var media = contentType.Split(';', 2)[0].Trim();
        if (!media.Equals("application/xml", StringComparison.OrdinalIgnoreCase) &&
            !media.Equals("text/xml", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(ApiOperationResolutionStatus.Unsupported, "contenttype.unsupported",
                "WSDL 2.0 operation selection requires an XML document.");
        }
        if (selector.Kind != ApiOperationSelectorKind.InterfaceOperation)
        {
            return Failure(ApiOperationResolutionStatus.Unsupported, "selector.unsupported",
                "WSDL 2.0 requires expanded interface/operation names, without an input/output overload suffix.");
        }
        var budget = new ValidationContext(new()
        {
            MaxDocumentBytes = Capabilities.MaxDocumentBytes,
            MaxTotalBytes = Capabilities.MaxTotalDocumentBytes,
            MaxDepth = Capabilities.MaxDocumentDepth,
            MaxNodes = Capabilities.MaxDocumentNodes,
            MaxWork = 8_388_608
        }, token);
        XNamespace ns = Wsdl20Namespace;
        var documents = new Dictionary<XElement, (string Namespace, Uri? Origin, HashSet<string> Imports)>();
        var includes = new Dictionary<XElement, List<XElement>>();
        var schemaContexts = new Dictionary<XElement, Dictionary<string, Wsdl20SchemaElementSelection>>();
        var schemaOrigins = new Dictionary<XElement, Uri?>();
        var schemaCopies = new Dictionary<XElement, XElement>();
        var schemaDependencies = new Dictionary<XElement, XElement[]>();
        var schemaNamespacePeers = new Dictionary<XElement, List<XElement>>();
        var externalSchemas = new Dictionary<Uri, XElement>();
        var unavailableSchemas = new HashSet<Uri>();
        var inlineSchemas = new List<XElement>();
        var loaded = new Dictionary<Uri, XElement>();
        var unavailable = new HashSet<Uri>();
        var incompleteNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var catalogs = new List<(string Namespace, int Depth)>();
        var references = 0;
        try
        {
            foreach (var value in selector.Tokens) { budget.Work(value.Length, "$/selector"); }
            CheckExpanded(selector.Tokens[1]);
            CheckExpanded(selector.Tokens[3]);
            var root = ReadWsdlXml(bytes, budget);
            if (documentUri is not null) { loaded.Add(documentUri, root); }
            await LoadAsync(root, documentUri, 0).ConfigureAwait(false);
            for (var index = 0; index < catalogs.Count; index++)
            {
                var catalog = catalogs[index];
                budget.Work();
                if (documents.Values.Any(document => document.Namespace == catalog.Namespace)) { continue; }
                await AcquireAsync(new Uri(catalog.Namespace, UriKind.Absolute), catalog.Namespace, null, catalog.Depth, false)
                    .ConfigureAwait(false);
            }
            foreach (var document in documents)
                foreach (var schema in document.Key.Elements(ns + "types").Elements(
                    XName.Get("schema", "http://www.w3.org/2001/XMLSchema")))
                {
                    inlineSchemas.Add(schema);
                    schemaOrigins.Add(schema, document.Value.Origin);
                    var copy = NamespaceCompleteWsdlOperation(schema, budget);
                    // Keep the WSDL ancestors' XML base on the detached schema, without applying its own base twice.
                    var ancestorBase = schema.Ancestors().FirstOrDefault(ancestor =>
                        ancestor.Attribute(XNamespace.Xml + "base") is not null);
                    if (ancestorBase is not null && (document.Value.Origin is not null ||
                        schema.Ancestors().Any(ancestor => ancestor.Attribute(XNamespace.Xml + "base") is { } xmlBase &&
                            Uri.TryCreate(xmlBase.Value, UriKind.Absolute, out _))))
                    {
                        copy.SetAttributeValue(XNamespace.Xml + "base",
                            WsdlImportUri(schema, "", document.Value.Origin, budget).AbsoluteUri);
                    }
                    schemaCopies.Add(schema, copy);
                }
            foreach (var document in documents.Keys)
                foreach (var element in document.Descendants())
                {
                    budget.Work();
                    if (element.Name.Namespace != ns && element.Attribute(ns + "required") is { } required)
                    {
                        var value = required.Value.Trim(' ', '\t', '\r', '\n');
                        ValidationContext.Require(value is "true" or "false" or "1" or "0", "$", "wsdl.extension_required",
                            "A WSDL extension's required attribute must be an XML Boolean.");
                        if (value is "true" or "1")
                        {
                            return Failure(ApiOperationResolutionStatus.Unsupported, "wsdl.extension",
                                "A required WSDL extension needs an explicitly implemented processing policy.");
                        }
                    }
                }
            foreach (var document in documents.Keys)
            {
                var needsSchemas = document.Elements(ns + "interface").SelectMany(element => element.Descendants())
                    .Any(element => element.Name.Namespace == ns && element.Name.LocalName is "input" or "output" or "fault" &&
                        element.Attribute("element") is { } attribute &&
                        !attribute.Value.Trim(' ', '\t', '\r', '\n').StartsWith('#'));
                if (needsSchemas) { _ = await SchemasAsync(document).ConfigureAwait(false); }
            }
            var interfaces = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var document in documents)
                foreach (var declaration in document.Key.Elements(ns + "interface"))
                {
                    var name = Expanded(document.Value.Namespace, Name(declaration), budget);
                    ValidationContext.Require(interfaces.TryAdd(name, declaration), "$/interface", "wsdl.duplicate_interface",
                        "A WSDL 2.0 interface name must be unique.");
                }
            var closures = new Dictionary<string, (Dictionary<string, Wsdl20OperationSelection> Operations,
                Dictionary<string, Wsdl20FaultComponent> Faults, int Depth)>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in interfaces.Keys) { _ = Visit(name, 0); }
            var selectedNamespace = selector.Tokens[1][1..selector.Tokens[1].LastIndexOf('}')];
            if (selectedNamespace != documents[root].Namespace && !documents[root].Imports.Contains(selectedNamespace) ||
                !closures.TryGetValue(selector.Tokens[1], out var closure) ||
                !closure.Operations.TryGetValue(selector.Tokens[3], out var selected))
            {
                if ((selectedNamespace == documents[root].Namespace || documents[root].Imports.Contains(selectedNamespace)) &&
                    incompleteNamespaces.Contains(selectedNamespace))
                {
                    return Failure(ApiOperationResolutionStatus.Indeterminate, "reference.unresolved",
                        "Unavailable namespace information prevents conclusive WSDL operation selection.");
                }
                return Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                    "No WSDL 2.0 operation matches the expanded interface and operation locator.");
            }
            var declaringDocument = interfaces[selected.DeclaringInterfaceName].Ancestors().Last();
            return new(ApiOperationResolutionStatus.Selected, documentUri: documents[declaringDocument].Origin,
                wsdl20Operation: selected);

            (Dictionary<string, Wsdl20OperationSelection> Operations, Dictionary<string, Wsdl20FaultComponent> Faults, int Depth)
                Visit(string name, int depth)
            {
                budget.Work(name.Length + 1L);
                CheckInheritanceDepth(depth);
                ValidationContext.Require(!active.Contains(name), "$/interface", "wsdl.inheritance_cycle",
                    "WSDL 2.0 interfaces cannot recursively extend one another.");
                if (closures.TryGetValue(name, out var cached))
                {
                    CheckInheritanceDepth(depth + cached.Depth);
                    return cached;
                }
                if (!interfaces.ContainsKey(name) && incompleteNamespaces.Contains(name[1..name.LastIndexOf('}')]))
                {
                    ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/interface", "reference.unresolved",
                        "Unavailable namespace information prevents resolution of an extended interface.");
                }
                ValidationContext.Require(interfaces.TryGetValue(name, out var declaration), "$/interface", "wsdl.interface",
                    "A WSDL 2.0 interface extends an undeclared interface.");
                var targetNamespace = documents[declaration.Ancestors().Last()].Namespace;
                active.Add(name);
                var result = new Dictionary<string, Wsdl20OperationSelection>(StringComparer.Ordinal);
                var faults = new Dictionary<string, Wsdl20FaultComponent>(StringComparer.Ordinal);
                var closureDepth = 0;
                var parents = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parent in Words(declaration.Attribute("extends")?.Value))
                {
                    var expanded = QName(declaration, parent);
                    ValidationContext.Require(parents.Add(expanded), "$/interface", "wsdl.duplicate_parent",
                        "An interface extends list cannot repeat an expanded interface QName.");
                    var inherited = Visit(expanded, depth + 1);
                    closureDepth = Math.Max(closureDepth, inherited.Depth + 1);
                    foreach (var operation in inherited.Operations.Values) { Add(operation); }
                    foreach (var fault in inherited.Faults.Values) { AddFault(fault); }
                }
                var declaredFaults = new HashSet<string>(StringComparer.Ordinal);
                foreach (var fault in declaration.Elements(ns + "fault"))
                {
                    var expanded = Expanded(targetNamespace, Name(fault), budget);
                    ValidationContext.Require(declaredFaults.Add(expanded), "$/interface/fault", "wsdl.duplicate_fault",
                        "An interface cannot declare two WSDL 2.0 faults with the same expanded name.");
                    var content = Content(fault);
                    AddFault(new(name, expanded, content.Model,
                        NamespaceCompleteWsdlOperation(fault, budget).ToString(SaveOptions.DisableFormatting), content.Element));
                }
                var declaredNames = new HashSet<string>(StringComparer.Ordinal);
                CheckStyles(declaration.Attribute("styleDefault")?.Value);
                foreach (var operation in declaration.Elements(ns + "operation"))
                {
                    var expanded = Expanded(targetNamespace, Name(operation), budget);
                    ValidationContext.Require(declaredNames.Add(expanded), "$/interface/operation", "wsdl.duplicate_operation",
                        "An interface cannot declare two WSDL 2.0 operations with the same expanded name.");
                    var pattern = operation.Attribute("pattern")?.Value.Trim(' ', '\t', '\r', '\n') ??
                        Wsdl20PatternPrefix + "in-out";
                    ValidationContext.Require(!HasRawUriWhitespaceOrControls(pattern) &&
                        Uri.IsWellFormedUriString(pattern, UriKind.Absolute), "$/interface/operation", "wsdl.pattern",
                        "A WSDL 2.0 message-exchange pattern must be an absolute IRI.");
                    CheckStyles(operation.Attribute("style")?.Value);
                    var elements = new List<Wsdl20SchemaElementSelection>();
                    var references = CheckMessages(operation, pattern, faults, elements);
                    Add(new(name, expanded, pattern, NamespaceCompleteWsdlOperation(operation, budget),
                        Words(operation.Attribute("style")?.Value ?? declaration.Attribute("styleDefault")?.Value),
                        references, elements));
                }
                active.Remove(name);
                closures.Add(name, (result, faults, closureDepth));
                return (result, faults, closureDepth);

                void AddFault(Wsdl20FaultComponent fault)
                {
                    budget.Work(fault.Name.Length + 1L);
                    if (faults.TryGetValue(fault.Name, out var previous))
                    {
                        ValidationContext.Require(ReferenceEquals(previous, fault), "$/interface", "wsdl.fault_conflict",
                            "Inherited faults with the same QName must identify equivalent components.");
                    }
                    else { faults.Add(fault.Name, fault); }
                }

                void CheckInheritanceDepth(int depth)
                {
                    if (depth > Capabilities.MaxReferenceDepth)
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/interface", "limit.depth",
                            "The WSDL interface inheritance limit was exceeded.");
                    }
                }

                void Add(Wsdl20OperationSelection operation)
                {
                    budget.Work(operation.OperationName.Length + 1L);
                    if (result.TryGetValue(operation.OperationName, out var previous))
                    {
                        // The parent interface is a component property; distinct declaring parents are not equivalent.
                        ValidationContext.Require(ReferenceEquals(previous, operation), "$/interface", "wsdl.operation_conflict",
                            "Inherited operations with the same QName must identify equivalent components.");
                    }
                    else { result.Add(operation.OperationName, operation); }
                }
            }

            void CheckExpanded(string name)
            {
                var close = name.LastIndexOf('}');
                ValidationContext.Require(name.StartsWith('{') && close > 1 && close < name.Length - 1,
                    "$/selector", "wsdl.expanded_name", "A WSDL 2.0 selector requires an expanded QName.");
                XmlConvert.VerifyNCName(name[(close + 1)..]);
            }

            string QName(XElement element, string value, bool wsdlComponent = true)
            {
                budget.Work(value.Length + 1L);
                value = value.Trim(' ', '\t', '\r', '\n');
                var parts = value.Split(':');
                ValidationContext.Require(parts.Length is 1 or 2, "$/interface", "wsdl.qname",
                    "A WSDL component reference requires an XML QName.");
                foreach (var part in parts)
                {
                    ValidationContext.Require(part.Length > 0, "$/interface", "wsdl.xml",
                        "An XML QName requires nonempty name parts.");
                    XmlConvert.VerifyNCName(part);
                }
                var namespaceName = parts.Length == 1 ? element.GetDefaultNamespace().NamespaceName :
                    element.GetNamespaceOfPrefix(parts[0])?.NamespaceName;
                ValidationContext.Require(namespaceName is not null, "$/interface", "wsdl.qname",
                    "A WSDL component reference uses an undeclared namespace prefix.");
                var containing = documents[element.Ancestors().Last()];
                ValidationContext.Require(!wsdlComponent || namespaceName == containing.Namespace || containing.Imports.Contains(namespaceName),
                    "$/interface", "wsdl.import_required",
                    "A foreign WSDL component reference requires an import in its authored document.");
                return Expanded(namespaceName, parts[^1], budget);
            }

            void CheckStyles(string? styles)
            {
                foreach (var style in Words(styles))
                {
                    budget.Work(style.Length + 1L);
                    ValidationContext.Require(Uri.IsWellFormedUriString(style, UriKind.Absolute), "$/operation", "wsdl.style",
                        "WSDL operation styles must be absolute IRIs.");
                }
            }

            Wsdl20FaultReferenceSelection[] CheckMessages(XElement operation, string pattern,
                Dictionary<string, Wsdl20FaultComponent> faults, List<Wsdl20SchemaElementSelection> elements)
            {
                var directions = pattern switch
                {
                    Wsdl20PatternPrefix + "in-only" or Wsdl20PatternPrefix + "robust-in-only" => new[] { "input" },
                    Wsdl20PatternPrefix + "out-only" or Wsdl20PatternPrefix + "robust-out-only" => new[] { "output" },
                    Wsdl20PatternPrefix + "in-out" or Wsdl20PatternPrefix + "in-opt-out" => ["input", "output"],
                    Wsdl20PatternPrefix + "out-in" or Wsdl20PatternPrefix + "out-opt-in" => ["output", "input"],
                    _ => null
                };
                if (directions is null)
                {
                    ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$/operation", "wsdl.pattern",
                        "This policy does not interpret custom WSDL message-exchange patterns.");
                }
                var children = operation.Elements().Where(e => e.Name.Namespace == ns &&
                    e.Name.LocalName != "documentation").ToArray();
                var messages = children.Where(e => e.Name.LocalName is not ("infault" or "outfault")).ToArray();
                ValidationContext.Require(messages.Length == directions.Length &&
                    messages.All(e => directions.Contains(e.Name.LocalName, StringComparer.Ordinal)), "$/operation", "wsdl.flow",
                    "WSDL message references must match the native pattern's placeholders.");
                var labels = new HashSet<string>(StringComparer.Ordinal);
                foreach (var message in messages)
                {
                    budget.Work();
                    var expected = message.Name.LocalName == "input" ? "In" : "Out";
                    var label = message.Attribute("messageLabel")?.Value.Trim(' ', '\t', '\r', '\n') ?? expected;
                    ValidationContext.Require(label == expected && labels.Add(label), "$/operation", "wsdl.message_label",
                        "WSDL message labels must identify distinct placeholders with their native directions.");
                    var content = Content(message);
                    if (content.Element is { } element) { elements.Add(element); }
                }
                var references = new List<Wsdl20FaultReferenceSelection>();
                var keys = new HashSet<(string Fault, string Label)>();
                var replaces = pattern is Wsdl20PatternPrefix + "in-out" or Wsdl20PatternPrefix + "out-in";
                var noFaults = pattern is Wsdl20PatternPrefix + "in-only" or Wsdl20PatternPrefix + "out-only";
                foreach (var reference in children.Where(e => e.Name.LocalName is "infault" or "outfault"))
                {
                    budget.Work();
                    var direction = reference.Name.LocalName == "infault" ? "in" : "out";
                    var applicable = directions.Where((value, index) => !noFaults &&
                        (replaces ? index > 0 && (value == "input" ? "in" : "out") == direction :
                            (value == "input" ? "in" : "out") != direction)).ToArray();
                    ValidationContext.Require(applicable.Length > 0, "$/operation/fault", "wsdl.fault_direction",
                        "A fault reference must obey the native pattern's fault propagation rule.");
                    var authoredName = reference.Attribute("ref")?.Value;
                    ValidationContext.Require(authoredName is not null, "$/operation/fault", "wsdl.fault_ref",
                        "A fault reference requires the QName of a visible fault component.");
                    var expanded = QName(reference, authoredName);
                    ValidationContext.Require(faults.TryGetValue(expanded, out var fault), "$/operation/fault", "wsdl.fault_ref",
                        "A fault reference must identify a fault in its declaring interface or an extended interface.");
                    var label = reference.Attribute("messageLabel")?.Value.Trim(' ', '\t', '\r', '\n');
                    if (label is null)
                    {
                        ValidationContext.Require(applicable.Length == 1, "$/operation/fault", "wsdl.fault_label",
                            "An ambiguous fault placeholder requires an explicit message label.");
                        label = applicable[0] == "input" ? "In" : "Out";
                    }
                    ValidationContext.Require(label.Length > 0, "$/operation/fault", "wsdl.xml",
                        "A fault message label requires a nonempty XML name.");
                    XmlConvert.VerifyNCName(label);
                    ValidationContext.Require(applicable.Any(value => (value == "input" ? "In" : "Out") == label),
                        "$/operation/fault", "wsdl.fault_label",
                        "A fault message label must identify a placeholder allowed by its native direction.");
                    ValidationContext.Require(keys.Add((expanded, label)), "$/operation/fault", "wsdl.duplicate_fault_ref",
                        "A fault component and message label combination must be unique within an operation.");
                    references.Add(new(fault.DeclaringInterface, fault.Name, fault.ContentModel, fault.Xml, direction, label,
                        fault.SchemaElement));
                    if (fault.SchemaElement is { } schemaElement) { elements.Add(schemaElement); }
                }
                return references.ToArray();
            }

            (string Model, Wsdl20SchemaElementSelection? Element) Content(XElement reference)
            {
                var authored = reference.Attribute("element")?.Value.Trim(' ', '\t', '\r', '\n') ?? "#other";
                if (authored is "#any" or "#none") { return (authored, null); }
                if (authored.StartsWith('#'))
                {
                    ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$/operation", "wsdl20.element",
                        "This WSDL 2.0 policy does not interpret other type systems.");
                }
                var name = QName(reference, authored, wsdlComponent: false);
                var containing = reference.Ancestors().Last();
                var schemas = schemaContexts[containing];
                ValidationContext.Require(schemas.TryGetValue(name, out var element), "$/operation", "wsdl20.schema_element",
                    "A message or fault element QName must identify a visible global XML Schema element.");
                return (name, element);
            }

            async ValueTask<Dictionary<string, Wsdl20SchemaElementSelection>> SchemasAsync(XElement document)
            {
                if (schemaContexts.TryGetValue(document, out var cached)) { return cached; }
                XNamespace xs = "http://www.w3.org/2001/XMLSchema";
                var visited = new HashSet<XElement>();
                var roots = new List<XElement>();
                var visible = new HashSet<(XElement Schema, string Namespace)>();
                var directImports = new List<XElement>();
                Collect(document);
                var importedRoots = new List<(string Namespace, XElement Schema)>();
                foreach (var import in directImports.OrderBy(import => import.Attribute("schemaLocation") is null))
                {
                    var expectedNamespace = import.Attribute("namespace")?.Value.Trim(' ', '\t', '\r', '\n');
                    ValidationContext.Require(!string.IsNullOrEmpty(expectedNamespace) &&
                        Uri.IsWellFormedUriString(expectedNamespace, UriKind.RelativeOrAbsolute) &&
                        !HasRawUriWhitespaceOrControls(expectedNamespace), "$/types/import", "wsdl20.schema_namespace",
                        "A direct WSDL schema import requires a nonempty, well-formed namespace.");
                    var dependency = await ResolveSchemaAsync(import, documents[import.Ancestors().Last()].Origin, 0)
                        .ConfigureAwait(false);
                    ValidationContext.Require(dependency.Attribute("targetNamespace") is not null &&
                        SchemaNamespace(dependency) == expectedNamespace, "$/types/import", "wsdl20.schema_namespace",
                        "A direct WSDL schema import requires a matching declared target namespace.");
                    importedRoots.Add((expectedNamespace, dependency));
                }
                foreach (var rootSchema in roots)
                {
                    await LoadSchemaAsync(rootSchema, 0).ConfigureAwait(false);
                    Expose(rootSchema, SchemaNamespace(rootSchema));
                }
                foreach (var (expectedNamespace, dependency) in importedRoots)
                {
                    roots.Add(dependency);
                    await LoadSchemaAsync(dependency, 0).ConfigureAwait(false);
                    Expose(dependency, SchemaNamespace(dependency));
                    foreach (var schema in inlineSchemas.Concat(externalSchemas.Values)
                        .Where(schema => SchemaNamespace(schema) == expectedNamespace).Distinct().ToArray())
                    {
                        roots.Add(schema);
                        await LoadSchemaAsync(schema, 0).ConfigureAwait(false);
                        Expose(schema, SchemaNamespace(schema));
                    }
                }
                var graph = new HashSet<XElement>();
                foreach (var rootSchema in roots)
                {
                    CheckDepth(rootSchema, 0, []);
                    Closure(rootSchema);
                }
                var ordered = graph.ToArray();
                var schemaDocuments = Array.AsReadOnly(ordered.Select(rootSchema =>
                    schemaCopies[rootSchema].ToString(SaveOptions.DisableFormatting)).ToArray());
                var schemaDocumentUris = Array.AsReadOnly(ordered.Select(rootSchema => schemaOrigins[rootSchema]).ToArray());
                foreach (var xml in schemaDocuments) { budget.Work(xml.Length); }
                var result = new Dictionary<string, Wsdl20SchemaElementSelection>(StringComparer.Ordinal);
                var symbols = new HashSet<(string Kind, string Name)>();
                foreach (var (schema, namespaceName) in visible)
                    foreach (var declaration in schema.Elements())
                    {
                        budget.Work();
                        if (declaration.Name.Namespace != xs ||
                            declaration.Name.LocalName is not ("element" or "simpleType" or "complexType")) { continue; }
                        var name = Expanded(namespaceName,
                            Name(declaration), budget);
                        var kind = declaration.Name.LocalName == "element" ? "element" : "type";
                        ValidationContext.Require(symbols.Add((kind, name)), "$/types", "wsdl20.schema_duplicate",
                            "Visible schemas cannot duplicate global elements or types within their namespace.");
                        if (kind != "element") { continue; }
                        result.Add(name, new(name,
                            NamespaceCompleteWsdlOperation(declaration, budget).ToString(SaveOptions.DisableFormatting),
                            schemaDocuments,
                            schemaOrigins[schema], schemaDocumentUris));
                    }
                var links = ordered.ToDictionary(source => schemaCopies[source],
                    source => schemaDependencies[source].Select(target => schemaCopies[target]).ToArray());
                new XmlSchemaSyntax(budget).ValidateEmbedded(ordered.Select(source => schemaCopies[source]).ToArray(), links);
                schemaContexts.Add(document, result);
                return result;

                void Collect(XElement entry)
                {
                    budget.Work();
                    if (!visited.Add(entry)) { return; }
                    var types = entry.Elements(ns + "types").ToArray();
                    ValidationContext.Require(types.Length <= 1, "$/types", "wsdl20.types",
                        "A WSDL description can contain at most one types element.");
                    foreach (var type in types)
                    {
                        foreach (var import in type.Elements(xs + "import"))
                        {
                            directImports.Add(import);
                        }
                        foreach (var schema in type.Elements(xs + "schema"))
                        {
                            roots.Add(schema);
                        }
                    }
                    foreach (var included in includes[entry]) { Collect(included); }
                }

                async ValueTask LoadSchemaAsync(XElement schema, int depth)
                {
                    budget.Work();
                    if (depth > Capabilities.MaxReferenceDepth)
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/types", "limit.depth",
                            "The schema dependency depth limit was exceeded.");
                    }
                    if (schemaDependencies.ContainsKey(schema)) { return; }
                    XNamespace xs = "http://www.w3.org/2001/XMLSchema";
                    var references = schema.Elements().Where(element => element.Name == xs + "include" ||
                        element.Name == xs + "import").ToArray();
                    if (schema.Elements(xs + "redefine").Any())
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$/types", "xsd.construct_unsupported",
                            "Schema redefinition is outside the bounded XSD compilation policy.");
                    }
                    var targets = new XElement[references.Length];
                    schemaDependencies.Add(schema, targets);
                    var namespacePeers = new List<XElement>();
                    schemaNamespacePeers.Add(schema, namespacePeers);
                    foreach (var index in Enumerable.Range(0, references.Length)
                        .OrderBy(index => references[index].Attribute("schemaLocation") is null))
                    {
                        var reference = references[index];
                        var target = await ResolveSchemaAsync(reference, schemaOrigins[schema], depth + 1).ConfigureAwait(false);
                        var include = reference.Name == xs + "include";
                        var expected = include ? SchemaNamespace(schema) :
                            reference.Attribute("namespace")?.Value.Trim(' ', '\t', '\r', '\n') ?? "";
                        ValidationContext.Require((SchemaNamespace(target) == expected ||
                            include && target.Attribute("targetNamespace") is null) &&
                            (include || expected != SchemaNamespace(schema)), "$/types", "wsdl20.schema_namespace",
                            "A schema dependency must obey its native include or import namespace rule.");
                        targets[index] = target;
                    }
                    for (var index = 0; index < references.Length; index++)
                    {
                        var reference = references[index];
                        var target = targets[index];
                        await LoadSchemaAsync(target, depth + 1).ConfigureAwait(false);
                        if (reference.Name == xs + "import")
                        {
                            var expected = reference.Attribute("namespace")?.Value.Trim(' ', '\t', '\r', '\n') ?? "";
                            var peers = inlineSchemas.Concat(externalSchemas.Values).Where(candidate =>
                                !ReferenceEquals(candidate, target) && SchemaNamespace(candidate) == expected).Distinct().ToArray();
                            foreach (var peer in peers)
                            {
                                namespacePeers.Add(peer);
                                await LoadSchemaAsync(peer, depth + 1).ConfigureAwait(false);
                            }
                        }
                    }
                }

                async ValueTask<XElement> ResolveSchemaAsync(XElement reference, Uri? origin, int depth)
                {
                    budget.Work();
                    var location = reference.Attribute("schemaLocation")?.Value.Trim(' ', '\t', '\r', '\n');
                    Uri uri;
                    if (location is null)
                    {
                        ValidationContext.Require(reference.Name.LocalName == "import", "$/types", "wsdl20.schema_location",
                            "An XML Schema include requires a schemaLocation.");
                        var namespaceName = reference.Attribute("namespace")?.Value.Trim(' ', '\t', '\r', '\n') ?? "";
                        var inline = inlineSchemas.FirstOrDefault(schema => SchemaNamespace(schema) == namespaceName);
                        if (inline is not null) { return inline; }
                        var acquiredSchema = externalSchemas.Values.FirstOrDefault(schema => SchemaNamespace(schema) == namespaceName);
                        if (acquiredSchema is not null) { return acquiredSchema; }
                        if (namespaceName.Length == 0)
                        {
                            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/types", "reference.unresolved",
                                "No explicitly supplied document resolves the no-namespace schema import.");
                        }
                        ValidationContext.Require(
                            !HasRawUriWhitespaceOrControls(namespaceName) &&
                            Uri.IsWellFormedUriString(namespaceName, UriKind.RelativeOrAbsolute), "$/types", "wsdl20.schema_namespace",
                            "A schema import namespace must be well formed.");
                        if (!Uri.IsWellFormedUriString(namespaceName, UriKind.Absolute))
                        {
                            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/types", "reference.unresolved",
                                "A relative namespace has no resolved schema or absolute catalog identifier.");
                        }
                        uri = new Uri(namespaceName, UriKind.Absolute);
                        origin = null;
                    }
                    else
                    {
                        if (location.Contains('#', StringComparison.Ordinal))
                        {
                            ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$/types", "wsdl20.schema_fragment",
                                "This schema policy does not yet select embedded schemas through location fragments.");
                        }
                        uri = WsdlImportUri(reference, location, origin, budget);
                        origin = uri;
                    }
                    if (depth > Capabilities.MaxReferenceDepth)
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/types", "limit.depth",
                            "The schema dependency depth limit was exceeded.");
                    }
                    if (externalSchemas.TryGetValue(uri, out var cached)) { return cached; }
                    ReadOnlyMemory<byte>? acquired = null;
                    if (!unavailableSchemas.Contains(uri))
                    {
                        if (++references > Capabilities.MaxReferencedDocuments)
                        {
                            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/types", "limit.references",
                                "The shared WSDL and schema dependency count was exceeded.");
                        }
                        token.ThrowIfCancellationRequested();
                        acquired = resolver is null ? null : await resolver(uri.AbsoluteUri, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }
                    if (acquired is null)
                    {
                        unavailableSchemas.Add(uri);
                        if (location is not null && reference.Name.LocalName == "import")
                        {
                            var catalogReference = new XElement(reference);
                            catalogReference.Attribute("schemaLocation")!.Remove();
                            return await ResolveSchemaAsync(catalogReference, null, depth).ConfigureAwait(false);
                        }
                        ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/types", "reference.unresolved",
                            "No explicitly supplied document resolves the schema dependency.");
                    }
                    var schemaRoot = ReadWsdlXml(acquired.Value, budget);
                    ValidationContext.Require(schemaRoot.Name == XName.Get("schema", "http://www.w3.org/2001/XMLSchema"),
                        "$/types", "xsd.root", "A schema dependency must be an XML Schema document.");
                    externalSchemas.Add(uri, schemaRoot);
                    schemaOrigins.Add(schemaRoot, origin);
                    schemaCopies.Add(schemaRoot, NamespaceCompleteWsdlOperation(schemaRoot, budget));
                    return schemaRoot;
                }

                static string SchemaNamespace(XElement schema) =>
                    schema.Attribute("targetNamespace")?.Value.Trim(' ', '\t', '\r', '\n') ?? "";

                void Expose(XElement schema, string namespaceName)
                {
                    budget.Work();
                    if (!visible.Add((schema, namespaceName))) { return; }
                    var children = schema.Elements().Where(element => element.Name == xs + "import" ||
                        element.Name == xs + "include").ToArray();
                    for (var index = 0; index < children.Length; index++)
                    {
                        if (children[index].Name == xs + "include")
                        {
                            var target = schemaDependencies[schema][index];
                            Expose(target, target.Attribute("targetNamespace") is null ? namespaceName : SchemaNamespace(target));
                        }
                    }
                }

                void Closure(XElement schema)
                {
                    budget.Work();
                    if (!graph.Add(schema)) { return; }
                    foreach (var dependency in schemaDependencies[schema]) { Closure(dependency); }
                    foreach (var peer in schemaNamespacePeers[schema]) { Closure(peer); }
                }

                void CheckDepth(XElement schema, int depth, HashSet<XElement> path)
                {
                    budget.Work();
                    if (!path.Add(schema)) { return; }
                    if (depth > Capabilities.MaxReferenceDepth)
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/types", "limit.depth",
                            "The schema dependency depth limit was exceeded, including cached dependencies.");
                    }
                    foreach (var dependency in schemaDependencies[schema]) { CheckDepth(dependency, depth + 1, path); }
                    foreach (var peer in schemaNamespacePeers[schema]) { CheckDepth(peer, depth + 1, path); }
                    path.Remove(schema);
                }
            }
        }
        catch (ValidationFailure failure)
        {
            return new(failure.Status switch
            {
                DocumentValidationStatus.Unsupported => ApiOperationResolutionStatus.Unsupported,
                DocumentValidationStatus.Indeterminate => ApiOperationResolutionStatus.Indeterminate,
                _ => ApiOperationResolutionStatus.Invalid
            }, failure.Diagnostic);
        }
        catch (XmlException)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "wsdl.xml",
                "The WSDL document or an XML name is malformed; DTD processing is prohibited.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(ApiOperationResolutionStatus.Denied, "dependency.denied",
                "Caller policy denied a required WSDL 2.0 import or include.");
        }

        async ValueTask LoadAsync(XElement entry, Uri? origin, int depth)
        {
            budget.Work();
            if (depth > Capabilities.MaxReferenceDepth)
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/import", "limit.depth",
                    "The WSDL document nesting limit was exceeded.");
            }
            ValidationContext.Require(entry.Name == ns + "description", "$", "wsdl.version",
                "WSDL 2.0 requires a description root in its WSDL namespace.");
            var targetNamespace = entry.Attribute("targetNamespace")?.Value.Trim(' ', '\t', '\r', '\n');
            ValidationContext.Require(targetNamespace is not null && !HasRawUriWhitespaceOrControls(targetNamespace) &&
                Uri.IsWellFormedUriString(targetNamespace, UriKind.Absolute), "$", "wsdl.namespace",
                "A WSDL 2.0 description requires an absolute target namespace.");
            var imports = new HashSet<string>(StringComparer.Ordinal);
            var hints = new HashSet<(string Namespace, string? Location)>();
            documents.Add(entry, (targetNamespace, origin, imports));
            includes.Add(entry, []);
            foreach (var reference in entry.Elements().Where(e => e.Name == ns + "include" || e.Name == ns + "import"))
            {
                budget.Work();
                var include = reference.Name == ns + "include";
                var expectedNamespace = include ? targetNamespace :
                    reference.Attribute("namespace")?.Value.Trim(' ', '\t', '\r', '\n');
                ValidationContext.Require(expectedNamespace is not null &&
                    !HasRawUriWhitespaceOrControls(expectedNamespace) &&
                    Uri.IsWellFormedUriString(expectedNamespace, UriKind.Absolute), "$/import", "wsdl.import_namespace",
                    "A WSDL 2.0 import requires an absolute namespace.");
                var location = reference.Attribute("location")?.Value.Trim(' ', '\t', '\r', '\n');
                ValidationContext.Require(!include || location is not null, "$/include", "wsdl.include",
                    "A WSDL 2.0 include requires a location.");
                if (!include)
                {
                    ValidationContext.Require(expectedNamespace != targetNamespace, "$/import", "wsdl.import_namespace",
                        "A WSDL 2.0 import must identify a foreign namespace.");
                    ValidationContext.Require(hints.Add((expectedNamespace, location)), "$/import", "wsdl.duplicate_import",
                        "Repeated imports for one namespace must have distinct location values.");
                    imports.Add(expectedNamespace);
                }
                if (location is null)
                {
                    catalogs.Add((expectedNamespace, depth + 1));
                    continue;
                }
                var uri = WsdlImportUri(reference, location, origin, budget);
                var dependency = await AcquireAsync(uri, expectedNamespace, uri, depth + 1, include).ConfigureAwait(false);
                if (include && dependency is not null) { includes[entry].Add(dependency); }
            }
        }

        async ValueTask<XElement?> AcquireAsync(Uri uri, string expectedNamespace, Uri? origin, int depth, bool required)
        {
            if (unavailable.Contains(uri))
            {
                Missing();
                return null;
            }
            if (!loaded.TryGetValue(uri, out var dependency))
            {
                if (++references > Capabilities.MaxReferencedDocuments)
                {
                    ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/import", "limit.references",
                        "The WSDL imported-document limit was exceeded.");
                }
                token.ThrowIfCancellationRequested();
                var acquired = resolver is null ? null : await resolver(uri.AbsoluteUri, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (acquired is null)
                {
                    unavailable.Add(uri);
                    Missing();
                    return null;
                }
                dependency = ReadWsdlXml(acquired.Value, budget);
                ValidationContext.Require(dependency.Name == ns + "description", "$/import", "wsdl.version",
                    "A WSDL 2.0 import or include must resolve to a WSDL 2.0 description.");
                CheckNamespace();
                loaded.Add(uri, dependency);
                await LoadAsync(dependency, origin, depth).ConfigureAwait(false);
            }
            else { CheckNamespace(); }
            return dependency;

            void Missing()
            {
                if (required)
                {
                    ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/include", "reference.unresolved",
                        "No explicitly supplied document resolves the required WSDL include.");
                }
                incompleteNamespaces.Add(expectedNamespace);
            }

            void CheckNamespace()
            {
                ValidationContext.Require(
                    dependency.Attribute("targetNamespace")?.Value.Trim(' ', '\t', '\r', '\n') == expectedNamespace,
                    "$/import", "wsdl.import_namespace",
                    "The acquired WSDL namespace contradicts its import or include.");
            }
        }

        static string Name(XElement element)
        {
            var value = element.Attribute("name")?.Value;
            ValidationContext.Require(value is not null, "$/name", "wsdl.name", "A WSDL component requires a name.");
            value = value.Trim(' ', '\t', '\r', '\n');
            ValidationContext.Require(value.Length > 0, "$/name", "wsdl.xml",
                "A WSDL component requires a nonempty XML name.");
            return XmlConvert.VerifyNCName(value);
        }

        static string[] Words(string? value) => value?.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
    }
}
