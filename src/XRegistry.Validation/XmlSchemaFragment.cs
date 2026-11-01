// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XRegistry.Validation;

internal static class XmlSchemaFragment
{
    internal static List<(string Id, string[] Steps)> Parse(string fragment, ValidationContext budget,
        string path, string unsupportedCode)
    {
        budget.Work(fragment.Length + 1L);
        if (fragment.Length == 0)
        {
            ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, unsupportedCode,
                "An empty fragment does not select an embedded schema.");
        }
        var parts = new List<(string Id, string[] Steps)>();
        if (!fragment.Contains('(', StringComparison.Ordinal))
        {
            XmlConvert.VerifyNCName(fragment);
            parts.Add((fragment, []));
            return parts;
        }
        var offset = 0;
        var understood = false;
        while (offset < fragment.Length)
        {
            var start = offset;
            while (offset < fragment.Length && fragment[offset] != '(') { offset++; }
            ValidationContext.Require(offset < fragment.Length, path, "xpointer.syntax",
                "Each scheme-based pointer part requires parenthesized data.");
            var scheme = fragment[start..offset];
            var names = scheme.Split(':');
            ValidationContext.Require(names.Length is 1 or 2, path, "xpointer.syntax",
                "An XPointer scheme name must be a QName.");
            foreach (var name in names) { XmlConvert.VerifyNCName(name); }
            offset++;
            var nesting = 1;
            var data = new StringBuilder();
            while (offset < fragment.Length && nesting > 0)
            {
                budget.Work();
                var character = fragment[offset++];
                if (character == '^')
                {
                    ValidationContext.Require(offset < fragment.Length && fragment[offset] is '^' or '(' or ')',
                        path, "xpointer.syntax", "An XPointer circumflex must escape a circumflex or parenthesis.");
                    data.Append(fragment[offset++]);
                }
                else if (character == '(') { nesting++; data.Append(character); }
                else if (character == ')')
                {
                    if (--nesting > 0) { data.Append(character); }
                }
                else { data.Append(character); }
            }
            ValidationContext.Require(nesting == 0, path, "xpointer.syntax",
                "An XPointer scheme's parentheses must be balanced.");
            if (scheme == "element")
            {
                understood = true;
                var tokens = data.ToString().Split('/');
                var id = tokens[0];
                var valid = id.Length > 0 || tokens.Length > 1;
                if (id.Length > 0)
                {
                    try { XmlConvert.VerifyNCName(id); }
                    catch (XmlException) { valid = false; }
                }
                foreach (var step in tokens.Skip(1))
                {
                    budget.Work(step.Length + 1L);
                    valid &= step.Length > 0 && step[0] is >= '1' and <= '9' &&
                        step.All(character => character is >= '0' and <= '9');
                }
                // Invalid element() data selects nothing; later pointer parts can still succeed.
                if (valid) { parts.Add((id, tokens.Skip(1).ToArray())); }
            }
            if (offset < fragment.Length)
            {
                while (offset < fragment.Length && fragment[offset] is ' ' or '\t' or '\r' or '\n') { offset++; }
                ValidationContext.Require(offset < fragment.Length, path, "xpointer.syntax",
                    "XPointer whitespace must separate pointer parts, not follow the final part.");
            }
        }
        if (!understood)
        {
            ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, unsupportedCode,
                "No supported XPointer scheme can select a schema; this policy supports element() and XML ID shorthand.");
        }
        return parts;
    }

    internal static XElement Select(XElement container, IReadOnlyList<(string Id, string[] Steps)> parts,
        ValidationContext budget, string path, string duplicateCode)
    {
        XElement? selected = null;
        foreach (var (id, steps) in parts)
        {
            budget.Work();
            XElement? candidate = container;
            var firstStep = 0;
            if (id.Length == 0)
            {
                if (steps[0] != "1") { continue; }
                firstStep = 1;
            }
            else
            {
                var matches = new List<XElement>();
                foreach (var element in container.DescendantsAndSelf())
                {
                    budget.Work();
                    if (element.Attribute(XNamespace.Xml + "id")?.Value.Trim(' ', '\t', '\r', '\n') == id ||
                        element.Name.NamespaceName == "http://www.w3.org/2001/XMLSchema" &&
                        element.Attribute("id")?.Value.Trim(' ', '\t', '\r', '\n') == id)
                    {
                        matches.Add(element);
                    }
                }
                ValidationContext.Require(matches.Count <= 1, path, duplicateCode,
                    "A schema fragment identifier must identify one element in its containing document.");
                candidate = matches.SingleOrDefault();
            }
            for (var index = firstStep; candidate is not null && index < steps.Length; index++)
            {
                budget.Work(steps[index].Length + 1L);
                if (!int.TryParse(steps[index], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
                {
                    candidate = null;
                    break;
                }
                var childIndex = 0;
                XElement? next = null;
                foreach (var child in candidate.Elements())
                {
                    budget.Work();
                    if (++childIndex == ordinal) { next = child; break; }
                }
                candidate = next;
            }
            if (candidate is not null) { selected = candidate; break; }
        }
        if (selected is null)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, path, "reference.unresolved",
                "The schema location fragment does not identify an element in its captured document.");
        }
        ValidationContext.Require(selected.Name == XName.Get("schema", "http://www.w3.org/2001/XMLSchema"),
            path, "xsd.root", "A schema location fragment must identify an XML Schema schema element.");
        return selected;
    }
}
