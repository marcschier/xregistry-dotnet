// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Xml;
using System.Xml.Linq;
using System.Collections.ObjectModel;

namespace XRegistry.Validation;

/// <summary>An independently owned WSDL 2.0 operation without binding or endpoint inference.</summary>
public sealed class Wsdl20OperationSelection
{
    internal Wsdl20OperationSelection(string declaringInterface, string operationName, string pattern, XElement operation,
        IEnumerable<string> styles)
    {
        DeclaringInterfaceName = declaringInterface;
        OperationName = operationName;
        MessageExchangePattern = pattern;
        OperationXml = operation.ToString(SaveOptions.DisableFormatting);
        Styles = Array.AsReadOnly(styles.Distinct(StringComparer.Ordinal).ToArray());
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
}

public static partial class ApiOperationDocumentResolver
{
    private const string Wsdl20Namespace = "http://www.w3.org/ns/wsdl";
    private const string Wsdl20PatternPrefix = Wsdl20Namespace + "/";

    private static ApiOperationResolutionResult ResolveWsdl20(string contentType, ReadOnlyMemory<byte> bytes,
        ApiOperationSelector selector, Uri? documentUri, CancellationToken token)
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
        try
        {
            foreach (var value in selector.Tokens) { budget.Work(value.Length, "$/selector"); }
            CheckExpanded(selector.Tokens[1]);
            CheckExpanded(selector.Tokens[3]);
            var root = ReadWsdlXml(bytes, budget);
            XNamespace ns = Wsdl20Namespace;
            ValidationContext.Require(root.Name == ns + "description", "$", "wsdl.version",
                "WSDL 2.0 requires a description root in its WSDL namespace.");
            var targetNamespace = root.Attribute("targetNamespace")?.Value;
            ValidationContext.Require(targetNamespace is not null &&
                !HasRawUriWhitespaceOrControls(targetNamespace) &&
                Uri.IsWellFormedUriString(targetNamespace, UriKind.Absolute), "$", "wsdl.namespace",
                "A WSDL 2.0 description requires an absolute target namespace.");
            if (root.Elements(ns + "import").Any() || root.Elements(ns + "include").Any())
            {
                return Failure(ApiOperationResolutionStatus.Unsupported, "wsdl20.import",
                    "This WSDL 2.0 policy selects components from a standalone description only.");
            }
            foreach (var element in root.Descendants())
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
            var interfaces = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var declaration in root.Elements(ns + "interface"))
            {
                var name = Expanded(targetNamespace, Name(declaration), budget);
                ValidationContext.Require(interfaces.TryAdd(name, declaration), "$/interface", "wsdl.duplicate_interface",
                    "A WSDL 2.0 interface name must be unique.");
            }
            var closures = new Dictionary<string, Dictionary<string, Wsdl20OperationSelection>>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in interfaces.Keys) { _ = Visit(name, 0); }
            if (!closures.TryGetValue(selector.Tokens[1], out var operations) ||
                !operations.TryGetValue(selector.Tokens[3], out var selected))
            {
                return Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                    "No WSDL 2.0 operation matches the expanded interface and operation locator.");
            }
            return new(ApiOperationResolutionStatus.Selected, documentUri: documentUri, wsdl20Operation: selected);

            Dictionary<string, Wsdl20OperationSelection> Visit(string name, int depth)
            {
                budget.Work(name.Length + 1L);
                if (depth > Capabilities.MaxReferenceDepth)
                {
                    ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/interface", "limit.depth",
                        "The WSDL interface inheritance limit was exceeded.");
                }
                ValidationContext.Require(!active.Contains(name), "$/interface", "wsdl.inheritance_cycle",
                    "WSDL 2.0 interfaces cannot recursively extend one another.");
                if (closures.TryGetValue(name, out var cached)) { return cached; }
                ValidationContext.Require(interfaces.TryGetValue(name, out var declaration), "$/interface", "wsdl.interface",
                    "A WSDL 2.0 interface extends an undeclared interface.");
                active.Add(name);
                var result = new Dictionary<string, Wsdl20OperationSelection>(StringComparer.Ordinal);
                var parents = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parent in Words(declaration.Attribute("extends")?.Value))
                {
                    var expanded = QName(declaration, parent);
                    ValidationContext.Require(parents.Add(expanded), "$/interface", "wsdl.duplicate_parent",
                        "An interface extends list cannot repeat an expanded interface QName.");
                    foreach (var operation in Visit(expanded, depth + 1).Values) { Add(operation); }
                }
                var declaredNames = new HashSet<string>(StringComparer.Ordinal);
                CheckStyles(declaration.Attribute("styleDefault")?.Value);
                foreach (var operation in declaration.Elements(ns + "operation"))
                {
                    var expanded = Expanded(targetNamespace, Name(operation), budget);
                    ValidationContext.Require(declaredNames.Add(expanded), "$/interface/operation", "wsdl.duplicate_operation",
                        "An interface cannot declare two WSDL 2.0 operations with the same expanded name.");
                    var pattern = operation.Attribute("pattern")?.Value ?? Wsdl20PatternPrefix + "in-out";
                    ValidationContext.Require(!HasRawUriWhitespaceOrControls(pattern) &&
                        Uri.IsWellFormedUriString(pattern, UriKind.Absolute), "$/interface/operation", "wsdl.pattern",
                        "A WSDL 2.0 message-exchange pattern must be an absolute IRI.");
                    CheckStyles(operation.Attribute("style")?.Value);
                    CheckMessages(operation, pattern);
                    Add(new(name, expanded, pattern, NamespaceCompleteWsdlOperation(operation, budget),
                        Words(operation.Attribute("style")?.Value ?? declaration.Attribute("styleDefault")?.Value)));
                }
                active.Remove(name);
                closures.Add(name, result);
                return result;

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

            string QName(XElement element, string value)
            {
                budget.Work(value.Length + 1L);
                var parts = value.Split(':');
                ValidationContext.Require(parts.Length is 1 or 2, "$/interface", "wsdl.qname",
                    "An interface reference requires an XML QName.");
                foreach (var part in parts) { XmlConvert.VerifyNCName(part); }
                var namespaceName = parts.Length == 1 ? element.GetDefaultNamespace().NamespaceName :
                    element.GetNamespaceOfPrefix(parts[0])?.NamespaceName;
                ValidationContext.Require(namespaceName is not null, "$/interface", "wsdl.qname",
                    "An interface reference uses an undeclared namespace prefix.");
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

            void CheckMessages(XElement operation, string pattern)
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
                var messages = operation.Elements().Where(e => e.Name.Namespace == ns &&
                    e.Name.LocalName != "documentation").ToArray();
                if (messages.Any(e => e.Name.LocalName is "infault" or "outfault"))
                {
                    ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$/operation", "wsdl20.fault",
                        "WSDL 2.0 fault-component resolution is not yet supported.");
                }
                ValidationContext.Require(messages.Length == directions.Length &&
                    messages.All(e => directions.Contains(e.Name.LocalName, StringComparer.Ordinal)), "$/operation", "wsdl.flow",
                    "WSDL message references must match the native pattern's placeholders.");
                var labels = new HashSet<string>(StringComparer.Ordinal);
                foreach (var message in messages)
                {
                    budget.Work();
                    var expected = message.Name.LocalName == "input" ? "In" : "Out";
                    var label = message.Attribute("messageLabel")?.Value ?? expected;
                    ValidationContext.Require(label == expected && labels.Add(label), "$/operation", "wsdl.message_label",
                        "WSDL message labels must identify distinct placeholders with their native directions.");
                    var element = message.Attribute("element")?.Value;
                    if (element is not ("#any" or "#none"))
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$/operation", "wsdl20.element",
                            "This WSDL 2.0 policy does not yet resolve schema element declarations or other type systems.");
                    }
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

        static string Name(XElement element)
        {
            var value = element.Attribute("name")?.Value;
            ValidationContext.Require(value is not null, "$/name", "wsdl.name", "A WSDL component requires a name.");
            return XmlConvert.VerifyNCName(value);
        }

        static string[] Words(string? value) => value?.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
    }
}
