// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Xml;
using System.Xml.Linq;

namespace XRegistry.Validation;

/// <summary>A WSDL 1.1 operation's native message-exchange pattern.</summary>
public enum WsdlMessageExchangePattern
{
    /// <summary>Receives one input message.</summary>
    OneWay,
    /// <summary>Receives input, then sends output.</summary>
    RequestResponse,
    /// <summary>Sends output, then receives input.</summary>
    SolicitResponse,
    /// <summary>Sends one output message.</summary>
    Notification
}

/// <summary>An independently owned abstract WSDL operation, without endpoint or binding selection.</summary>
public sealed class WsdlOperationSelection
{
    internal WsdlOperationSelection(string interfaceName, string name, string? inputName, string? outputName,
        string? inputMessage, string? outputMessage, WsdlMessageExchangePattern pattern, XElement xml,
        IEnumerable<string> faultMessages)
    {
        InterfaceName = interfaceName;
        OperationName = name;
        InputName = inputName;
        OutputName = outputName;
        InputMessage = inputMessage;
        OutputMessage = outputMessage;
        MessageExchangePattern = pattern;
        OperationXml = xml.ToString(SaveOptions.DisableFormatting);
        FaultMessages = Array.AsReadOnly(faultMessages.ToArray());
    }

    /// <summary>Gets the expanded port type name, independent of XML prefixes.</summary>
    public string InterfaceName { get; }
    /// <summary>Gets the exact local operation name.</summary>
    public string OperationName { get; }
    /// <summary>Gets the effective input name, including WSDL defaults; null means absent input.</summary>
    public string? InputName { get; }
    /// <summary>Gets the effective output name, including WSDL defaults; null means absent output.</summary>
    public string? OutputName { get; }
    /// <summary>Gets the expanded input message QName, or null for absent input.</summary>
    public string? InputMessage { get; }
    /// <summary>Gets the expanded output message QName, or null for absent output.</summary>
    public string? OutputMessage { get; }
    /// <summary>Gets the operation's native transmission pattern.</summary>
    public WsdlMessageExchangePattern MessageExchangePattern { get; }
    /// <summary>Gets expanded fault message QNames in declaration order.</summary>
    public ReadOnlyCollection<string> FaultMessages { get; }
    /// <summary>Gets retained native operation XML with in-scope namespace declarations.</summary>
    public string OperationXml { get; }
}

public static partial class ApiOperationDocumentResolver
{
    private const string Wsdl11Namespace = "http://schemas.xmlsoap.org/wsdl/";

    private static async ValueTask<ApiOperationResolutionResult> ResolveWsdl11Async(string contentType, ReadOnlyMemory<byte> bytes,
        ApiOperationSelector selector, Uri? documentUri, ApiOperationDocumentDependencyResolver? resolver,
        CancellationToken token)
    {
        var media = contentType.Split(';', 2)[0].Trim();
        if (!media.Equals("application/xml", StringComparison.OrdinalIgnoreCase) &&
            !media.Equals("text/xml", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(ApiOperationResolutionStatus.Unsupported, "contenttype.unsupported",
                "WSDL 1.1 operation selection requires an XML document.");
        }
        if (selector.Kind is not (ApiOperationSelectorKind.InterfaceOperation or
            ApiOperationSelectorKind.InterfaceOperationInputOutput))
        {
            return Failure(ApiOperationResolutionStatus.Unsupported, "selector.unsupported",
                "WSDL 1.1 requires an interface operation locator.");
        }
        var budget = new ValidationContext(new()
        {
            MaxDocumentBytes = Capabilities.MaxDocumentBytes,
            MaxTotalBytes = Capabilities.MaxTotalDocumentBytes,
            MaxDepth = Capabilities.MaxDocumentDepth,
            MaxNodes = Capabilities.MaxDocumentNodes,
            MaxWork = 8_388_608
        }, token);
        var documents = new List<(XElement Root, Uri? Origin)>();
        var loaded = new Dictionary<Uri, XElement>();
        var active = new HashSet<Uri>();
        var references = 0;
        try
        {
            foreach (var value in selector.Tokens) { budget.Work(value.Length, "$/selector"); }
            var interfaceToken = selector.Tokens[1];
            var close = interfaceToken.LastIndexOf('}');
            ValidationContext.Require(interfaceToken.StartsWith('{') && close >= 1 && close < interfaceToken.Length - 1,
                "$/selector", "wsdl.interface_name", "A WSDL interface selector requires an expanded QName.");
            XmlConvert.VerifyNMTOKEN(interfaceToken[(close + 1)..]);
            var root = ReadWsdlXml(bytes, budget);
            XNamespace ns = Wsdl11Namespace;
            ValidationContext.Require(root.Name == ns + "definitions", "$", "wsdl.version",
                "WSDL 1.1 requires a definitions root in its WSDL namespace.");
            await LoadAsync(root, documentUri, 0).ConfigureAwait(false);
            foreach (var document in documents)
            foreach (var element in document.Root.Descendants())
            {
                budget.Work();
                if (element.Name.Namespace == ns || element.Attribute(ns + "required") is not { } required) { continue; }
                budget.Work(required.Value.Length);
                var requiredValue = required.Value.Trim(' ', '\t', '\n', '\r');
                ValidationContext.Require(requiredValue is "true" or "false" or "1" or "0", "$", "wsdl.extension_required",
                    "A WSDL extension's required attribute must be an XML Boolean.");
                if (requiredValue is "true" or "1")
                {
                    return Failure(ApiOperationResolutionStatus.Unsupported, "wsdl.extension",
                        "A required WSDL extension needs an explicitly implemented processing policy.");
                }
            }
            var messages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var document in documents)
            foreach (var message in document.Root.Elements(ns + "message"))
            {
                budget.Work();
                ValidationContext.Require(messages.Add(Expanded(document.Root.Attribute("targetNamespace")?.Value ?? "",
                    WsdlName(message), budget)),
                    "$/message", "wsdl.duplicate_message", "A WSDL message name must be unique.");
            }
            var ports = new HashSet<string>(StringComparer.Ordinal);
            var operations = new List<(WsdlOperationSelection Operation, Uri? Origin)>();
            foreach (var document in documents)
            foreach (var port in document.Root.Elements(ns + "portType"))
            {
                budget.Work();
                var interfaceName = Expanded(document.Root.Attribute("targetNamespace")?.Value ?? "", WsdlName(port), budget);
                ValidationContext.Require(ports.Add(interfaceName), "$/portType", "wsdl.duplicate_interface",
                    "A WSDL port type name must be unique.");
                var identities = new HashSet<(string, string?, string?)>();
                var messageNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var operation in port.Elements(ns + "operation"))
                {
                    budget.Work();
                    var selected = ReadWsdlOperation(interfaceName, operation, messages, budget);
                    ValidationContext.Require(identities.Add((selected.OperationName, selected.InputName, selected.OutputName)),
                        "$/portType/operation", "wsdl.duplicate_operation",
                        "Overloaded WSDL operations must have distinguishable effective input/output names.");
                    foreach (var messageName in new[] { selected.InputName, selected.OutputName })
                    {
                        if (messageName is null) { continue; }
                        budget.Work(messageName.Length + 1L);
                        ValidationContext.Require(messageNames.Add(messageName), "$/portType/operation", "wsdl.message_name",
                            "Effective input and output names must be unique within a WSDL port type.");
                    }
                    if (interfaceName == selector.Tokens[1] && selected.OperationName == selector.Tokens[3] &&
                        (selector.Kind != ApiOperationSelectorKind.InterfaceOperationInputOutput ||
                            (selected.InputName ?? "!") == selector.Tokens[5] &&
                            (selected.OutputName ?? "!") == selector.Tokens[7]))
                    {
                        operations.Add((selected, document.Origin));
                    }
                }
            }
            return operations.Count switch
            {
                0 => Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                    "No WSDL operation matches the exact interface and operation locator."),
                1 => new(ApiOperationResolutionStatus.Selected, documentUri: operations[0].Origin,
                    wsdlOperation: operations[0].Operation),
                _ => Failure(ApiOperationResolutionStatus.Ambiguous, "selection.ambiguous",
                    "A name-only WSDL locator matches overloaded operations; select their effective input/output names.")
            };
        }
        catch (ValidationFailure error)
        {
            return new(error.Status switch
            {
                DocumentValidationStatus.Indeterminate => ApiOperationResolutionStatus.Indeterminate,
                DocumentValidationStatus.Unsupported => ApiOperationResolutionStatus.Unsupported,
                _ => ApiOperationResolutionStatus.Invalid
            }, error.Diagnostic);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(ApiOperationResolutionStatus.Denied, "dependency.denied",
                "Caller policy denied a required WSDL import.");
        }
        catch (XmlException)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "wsdl.xml",
                "The WSDL document or an XML name is malformed; DTD processing is prohibited.", "$");
        }

        async ValueTask LoadAsync(XElement entry, Uri? origin, int depth)
        {
            budget.Work();
            if (depth > Capabilities.MaxReferenceDepth)
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/import", "limit.depth",
                    "The WSDL import nesting limit was exceeded.");
            }
            XNamespace ns = Wsdl11Namespace;
            if (entry.Attribute("name") is { } documentName) { XmlConvert.VerifyNCName(documentName.Value); }
            if (entry.Attribute("targetNamespace") is { } targetNamespace)
            {
                budget.Work(targetNamespace.Value.Length + 1L);
                ValidationContext.Require(!HasRawUriWhitespaceOrControls(targetNamespace.Value) &&
                    Uri.IsWellFormedUriString(targetNamespace.Value, UriKind.Absolute), "$", "wsdl.namespace",
                    "A WSDL target namespace must be a well-formed absolute URI.");
            }
            documents.Add((entry, origin));
            if (origin is not null) { active.Add(origin); loaded.Add(origin, entry); }
            foreach (var import in entry.Elements(ns + "import"))
            {
                budget.Work();
                var location = import.Attribute("location")?.Value;
                var expectedNamespace = import.Attribute("namespace")?.Value;
                ValidationContext.Require(location is not null && expectedNamespace is not null, "$/import", "wsdl.import",
                    "A WSDL 1.1 import requires location and namespace attributes.");
                var importUri = WsdlImportUri(import, location, origin, budget);
                if (active.Contains(importUri))
                {
                    ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/import", "dependency.cycle",
                        "The WSDL import graph contains a cycle.");
                }
                if (!loaded.TryGetValue(importUri, out var dependency))
                {
                    if (++references > Capabilities.MaxReferencedDocuments)
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/import", "limit.references",
                            "The WSDL imported-document limit was exceeded.");
                    }
                    token.ThrowIfCancellationRequested();
                    var acquired = resolver is null ? null :
                        await resolver(importUri.AbsoluteUri, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (acquired is null)
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/import", "reference.unresolved",
                            "No explicitly supplied document resolves the WSDL import.");
                    }
                    dependency = ReadWsdlXml(acquired.Value, budget);
                    if (dependency.Name == ns + "definitions")
                    {
                        ValidationContext.Require((dependency.Attribute("targetNamespace")?.Value ?? "") == expectedNamespace,
                            "$/import", "wsdl.import_namespace", "The imported WSDL target namespace contradicts its import.");
                        await LoadAsync(dependency, importUri, depth + 1).ConfigureAwait(false);
                    }
                    else
                    {
                        if (dependency.Name != XName.Get("schema", "http://www.w3.org/2001/XMLSchema"))
                        {
                            ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$/import", "wsdl.import_kind",
                                "This WSDL import policy supports WSDL 1.1 and XML Schema documents only.");
                        }
                        ValidationContext.Require((dependency.Attribute("targetNamespace")?.Value ?? "") == expectedNamespace,
                            "$/import", "wsdl.import_namespace", "The imported schema target namespace contradicts its import.");
                        loaded.Add(importUri, dependency);
                    }
                }
                ValidationContext.Require((dependency.Attribute("targetNamespace")?.Value ?? "") == expectedNamespace,
                    "$/import", "wsdl.import_namespace", "A reused import contradicts the acquired target namespace.");
            }
            if (origin is not null) { active.Remove(origin); }
        }
    }

    private static XElement ReadWsdlXml(ReadOnlyMemory<byte> bytes, ValidationContext budget)
    {
        budget.Bytes(bytes.Length, "$");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = Capabilities.MaxDocumentBytes
        };
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using (var reader = XmlReader.Create(stream, settings))
        {
            while (reader.Read())
            {
                budget.Node(reader.Depth + 1);
                budget.Work(reader.AttributeCount + 1);
            }
        }
        stream.Position = 0;
        using var xmlReader = XmlReader.Create(stream, settings);
        return XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace).Root!;
    }

    private static Uri WsdlImportUri(XElement import, string location, Uri? origin, ValidationContext budget)
    {
        var baseUri = origin;
        foreach (var element in import.AncestorsAndSelf().Reverse())
        {
            if (element.Attribute(XNamespace.Xml + "base") is { } xmlBase)
            {
                baseUri = Resolve(xmlBase.Value, baseUri);
            }
        }
        return Resolve(location, baseUri);

        Uri Resolve(string value, Uri? relativeTo)
        {
            budget.Work(value.Length + 1L);
            ValidationContext.Require(!HasRawUriWhitespaceOrControls(value) &&
                Uri.IsWellFormedUriString(value, UriKind.RelativeOrAbsolute) &&
                !value.Contains('#', StringComparison.Ordinal),
                "$/import", "dependency.uri", "A WSDL import retrieval URI must be well formed and fragment-free.");
            if (!value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out var absolute)) { return absolute; }
            if (relativeTo is null)
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/import", "dependency.base",
                    "A relative WSDL import requires a captured absolute document base.");
            }
            if (!Uri.TryCreate(relativeTo, value, out var resolved))
            {
                ValidationContext.Fail(DocumentValidationStatus.Invalid, "$/import", "dependency.uri",
                    "The WSDL import URI cannot be resolved against its base.");
            }
            return resolved;
        }
    }

    private static WsdlOperationSelection ReadWsdlOperation(string interfaceName, XElement operation,
        HashSet<string> messages, ValidationContext budget)
    {
        XNamespace ns = Wsdl11Namespace;
        var name = WsdlName(operation);
        var flow = operation.Elements().Where(e => e.Name.NamespaceName == Wsdl11Namespace &&
            e.Name.LocalName != "documentation").ToArray();
        var input = flow.Where(e => e.Name == ns + "input").ToArray();
        var output = flow.Where(e => e.Name == ns + "output").ToArray();
        ValidationContext.Require(input.Length <= 1 && output.Length <= 1 &&
            input.Length + output.Length > 0 && flow.All(e => e.Name.LocalName is "input" or "output" or "fault"),
            "$/portType/operation", "wsdl.flow", "A WSDL operation requires one valid native message-exchange pattern.");
        var faults = flow.Where(e => e.Name == ns + "fault").ToArray();
        var twoWay = input.Length == 1 && output.Length == 1;
        ValidationContext.Require(twoWay || faults.Length == 0, "$/portType/operation", "wsdl.flow",
            "One-way and notification operations cannot declare faults.");
        ValidationContext.Require(flow.Take(input.Length + output.Length).All(e => e.Name.LocalName is "input" or "output"),
            "$/portType/operation", "wsdl.flow", "Fault declarations must follow input/output declarations.");
        var requestFirst = flow[0].Name == ns + "input";
        var pattern = twoWay ? requestFirst ? WsdlMessageExchangePattern.RequestResponse :
            WsdlMessageExchangePattern.SolicitResponse : requestFirst ? WsdlMessageExchangePattern.OneWay :
            WsdlMessageExchangePattern.Notification;
        string? inputName = input.Length == 0 ? null : input[0].Attribute("name")?.Value ??
            (twoWay ? name + (requestFirst ? "Request" : "Response") : name);
        string? outputName = output.Length == 0 ? null : output[0].Attribute("name")?.Value ??
            (twoWay ? name + (requestFirst ? "Response" : "Solicit") : name);
        if (inputName is not null) { XmlConvert.VerifyNMTOKEN(inputName); }
        if (outputName is not null) { XmlConvert.VerifyNMTOKEN(outputName); }
        var faultNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fault in faults)
        {
            budget.Work();
            ValidationContext.Require(faultNames.Add(WsdlName(fault)), "$/portType/operation/fault", "wsdl.fault",
                "WSDL fault names must be unique within an operation.");
        }
        return new(interfaceName, name, inputName, outputName,
            input.Length == 0 ? null : Message(input[0]), output.Length == 0 ? null : Message(output[0]),
            pattern, NamespaceCompleteWsdlOperation(operation, budget), faults.Select(Message).ToArray());

        string Message(XElement element)
        {
            var value = element.Attribute("message")?.Value;
            ValidationContext.Require(!string.IsNullOrEmpty(value), "$/portType/operation", "wsdl.message",
                "Every input/output/fault declaration requires a message QName.");
            var parts = value.Split(':');
            ValidationContext.Require(parts.Length is 1 or 2, "$/portType/operation", "wsdl.qname",
                "A WSDL message reference must be an XML QName.");
            foreach (var part in parts) { XmlConvert.VerifyNCName(part); }
            var namespaceName = parts.Length == 1 ? element.GetDefaultNamespace().NamespaceName :
                element.GetNamespaceOfPrefix(parts[0])?.NamespaceName;
            ValidationContext.Require(namespaceName is not null, "$/portType/operation", "wsdl.qname",
                "A WSDL QName uses an undeclared namespace prefix.");
            var expanded = Expanded(namespaceName, parts[^1], budget);
            ValidationContext.Require(messages.Contains(expanded), "$/portType/operation", "wsdl.message",
                "A WSDL operation references an undeclared message.");
            return expanded;
        }
    }

    private static XElement NamespaceCompleteWsdlOperation(XElement operation, ValidationContext budget)
    {
        var copy = new XElement(operation);
        foreach (var ancestor in operation.AncestorsAndSelf())
        {
            foreach (var attribute in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                budget.Work((long)attribute.Name.LocalName.Length + attribute.Value.Length + 1);
                if (copy.Attribute(attribute.Name) is null) { copy.Add(new XAttribute(attribute)); }
            }
        }
        return copy;
    }

    private static string WsdlName(XElement element)
    {
        var value = element.Attribute("name")?.Value;
        ValidationContext.Require(!string.IsNullOrEmpty(value), "$/name", "wsdl.name",
            "A WSDL component requires a non-empty name.");
        return XmlConvert.VerifyNMTOKEN(value);
    }

    private static string Expanded(string namespaceName, string localName, ValidationContext budget)
    {
        budget.Work((long)namespaceName.Length + localName.Length + 2);
        return "{" + namespaceName + "}" + localName;
    }
}
