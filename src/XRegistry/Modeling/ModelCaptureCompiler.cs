// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;

namespace XRegistry;

internal sealed class ModelCaptureCompiler(RegistryJson source, RegistryModelCompilationOptions options,
    CancellationToken cancellationToken)
{
    private readonly Dictionary<string, CaptureNode> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private int _work;

    internal static void Validate(RegistryJson source, RegistryJson resolved,
        RegistryModelCompilationOptions options, CancellationToken cancellationToken)
    {
        if (source.RootElement.ValueKind != JsonValueKind.Object || resolved.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw Diagnostics.Error("model_error", "", "Captured model sources must be JSON objects.");
        }

        RegistryJson.FromElement(source.RootElement, options.JsonLimits);
        RegistryJson.FromElement(resolved.RootElement, options.JsonLimits);
        var validator = new ModelCaptureCompiler(source, options, cancellationToken);
        validator.Collect(source.RootElement, "");
        validator.CheckReferences("");
        validator.CheckResolved(resolved.RootElement, "");
        validator.Compare(validator.KnownValue(""), resolved.RootElement, "");
    }

    private void Work(string path)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++_work > options.MaxExpandedNodes)
        {
            throw Diagnostics.Error("model_expansion_limit", path, "Captured model validation exhausted its work budget.");
        }
    }

    private void Collect(JsonElement value, string path)
    {
        Work(path);
        var node = new CaptureNode(value);
        _nodes.Add(path, node);
        if (value.ValueKind == JsonValueKind.Object)
        {
            var single = value.TryGetProperty("$include", out var include);
            var multiple = value.TryGetProperty("$includes", out var includes);
            if (single && multiple)
            {
                throw Diagnostics.Error("model_error", path, "$include and $includes cannot coexist in one object.");
            }
            if (single) { AddReference(node, include, Diagnostics.At(path, "$include")); }
            if (multiple)
            {
                if (includes.ValueKind != JsonValueKind.Array)
                {
                    throw Diagnostics.Error("model_error", Diagnostics.At(path, "$includes"), "$includes must be an array of references.");
                }
                var index = 0;
                foreach (var reference in includes.EnumerateArray())
                {
                    AddReference(node, reference, Diagnostics.At(Diagnostics.At(path, "$includes"),
                        (index++).ToString(CultureInfo.InvariantCulture)));
                }
            }
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "$include" or "$includes") { continue; }
                var child = Diagnostics.At(path, property.Name);
                node.Children.Add(child);
                Collect(property.Value, child);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var child = Diagnostics.At(path, (index++).ToString(CultureInfo.InvariantCulture));
                node.Children.Add(child);
                Collect(item, child);
            }
        }
    }

    private void AddReference(CaptureNode node, JsonElement value, string path)
    {
        Work(path);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Diagnostics.Error("model_error", path, "An include reference must be a string.");
        }
        var reference = value.GetString()!;
        if (!UriSyntax.IsReference(reference, out _))
        {
            throw Diagnostics.Error("model_error", path, "An include must be a valid URI reference.");
        }
        var hash = reference.IndexOf('#');
        var document = hash < 0 ? reference : reference[..hash];
        var fragment = hash < 0 ? "" : ModelIncludes.DecodeFragment(reference[(hash + 1)..], path);
        if (fragment.Length != 0 && fragment[0] != '/')
        {
            throw Diagnostics.Error("invalid_json_pointer", path, "An include fragment must be an RFC6901 pointer.");
        }
        for (var index = 0; index < fragment.Length; index++)
        {
            if (fragment[index] == '~' && (++index >= fragment.Length || fragment[index] is not ('0' or '1')))
            {
                throw Diagnostics.Error("invalid_json_pointer", path, "An include pointer contains an invalid tilde escape.");
            }
        }
        if (document.Length == 0 || options.SourceUri is { } origin &&
            Uri.TryCreate(origin, document, out var target) && target == origin)
        {
            var selected = ModelIncludes.Select(source.RootElement, fragment, path);
            if (selected.ValueKind != JsonValueKind.Object)
            {
                throw Diagnostics.Error("model_error", path, "An include must reference an object or map.");
            }
            node.References.Add((fragment, path));
        }
        else
        {
            node.References.Add((null, path));
        }
    }

    private int CheckReferences(string path, int includeDepth = 0)
    {
        Work(path);
        var node = _nodes[path];
        if (node.IncludeDepth is { } cached)
        {
            if (includeDepth + cached > options.MaxIncludeDepth)
            {
                throw Diagnostics.Error("model_expansion_limit", path, "Captured local includes exceed their depth budget.");
            }
            return cached;
        }
        if (!_active.Add(path))
        {
            throw Diagnostics.Error("model_include_cycle", path, "Captured local includes contain a cycle.");
        }
        var depth = 0;
        foreach (var child in node.Children) { depth = Math.Max(depth, CheckReferences(child, includeDepth)); }
        foreach (var reference in node.References)
        {
            if (reference.Target is null) { continue; }
            if (includeDepth >= options.MaxIncludeDepth)
            {
                throw Diagnostics.Error("model_expansion_limit", reference.Path, "Captured local includes exceed their depth budget.");
            }
            if (!_nodes.ContainsKey(reference.Target))
            {
                throw Diagnostics.Error("model_error", reference.Path, "An include cannot select its own directive storage.");
            }
            depth = Math.Max(depth, 1 + CheckReferences(reference.Target, includeDepth + 1));
            if (includeDepth + depth > options.MaxIncludeDepth)
            {
                throw Diagnostics.Error("model_expansion_limit", reference.Path, "Captured local includes exceed their depth budget.");
            }
        }
        _active.Remove(path);
        node.IncludeDepth = depth;
        return depth;
    }

    private void CheckResolved(JsonElement value, string path)
    {
        Work(path);
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "$include" or "$includes")
                {
                    throw Diagnostics.Error("model_error", Diagnostics.At(path, property.Name), "Captured resolved source still contains includes.");
                }
                CheckResolved(property.Value, Diagnostics.At(path, property.Name));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                CheckResolved(item, Diagnostics.At(path, (index++).ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private KnownCaptureValue KnownValue(string path)
    {
        Work(path);
        var node = _nodes[path];
        if (node.Known is { } cached) { return cached; }
        var result = new KnownCaptureValue(node.Value);
        if (result.Members is not null)
        {
            foreach (var property in node.Value.EnumerateObject())
            {
                if (property.Name is "$include" or "$includes") { continue; }
                result.Members.Add(property.Name, KnownValue(Diagnostics.At(path, property.Name)));
            }
            var earlierUnknown = false;
            foreach (var reference in node.References)
            {
                Work(reference.Path);
                if (reference.Target is null)
                {
                    result.Open = true;
                    earlierUnknown = true;
                    continue;
                }
                var included = KnownValue(reference.Target);
                if (!earlierUnknown)
                {
                    foreach (var member in included.Members!)
                    {
                        Work(reference.Path);
                        result.Members.TryAdd(member.Key, member.Value);
                    }
                }
                result.Open |= included.Open;
                earlierUnknown |= included.Open;
            }
        }
        else if (result.Items is not null)
        {
            foreach (var child in node.Children) { result.Items.Add(KnownValue(child)); }
        }
        node.Known = result;
        return result;
    }

    private void Compare(KnownCaptureValue authored, JsonElement resolved, string path)
    {
        Work(path);
        if (authored.Value.ValueKind != resolved.ValueKind) { throw Mismatch(path); }
        if (authored.Members is not null)
        {
            foreach (var property in authored.Members)
            {
                var at = Diagnostics.At(path, property.Key);
                if (!resolved.TryGetProperty(property.Key, out var value)) { throw Mismatch(at); }
                Compare(property.Value, value, at);
            }
            if (!authored.Open && authored.Members.Count != resolved.EnumerateObject().Count()) { throw Mismatch(path); }
        }
        else if (authored.Items is not null)
        {
            if (authored.Items.Count != resolved.GetArrayLength()) { throw Mismatch(path); }
            for (var index = 0; index < authored.Items.Count; index++)
            {
                Compare(authored.Items[index], resolved[index], Diagnostics.At(path, index.ToString(CultureInfo.InvariantCulture)));
            }
        }
        else if (authored.Value.ValueKind == JsonValueKind.Number)
        {
            if (!RegistryNumber.FromElement(authored.Value).Equals(RegistryNumber.FromElement(resolved))) { throw Mismatch(path); }
        }
        else if (authored.Value.ValueKind == JsonValueKind.String && authored.Value.GetString() != resolved.GetString())
        {
            throw Mismatch(path);
        }
    }

    private static RegistryException Mismatch(string path) =>
        Diagnostics.Error("model_error", path, "Captured resolved source contradicts the original model source.");

    private sealed class CaptureNode(JsonElement value)
    {
        internal JsonElement Value { get; } = value;
        internal List<string> Children { get; } = [];
        internal List<(string? Target, string Path)> References { get; } = [];
        internal int? IncludeDepth { get; set; }
        internal KnownCaptureValue? Known { get; set; }
    }

    private sealed class KnownCaptureValue(JsonElement value)
    {
        internal JsonElement Value { get; } = value;
        internal Dictionary<string, KnownCaptureValue>? Members { get; } =
            value.ValueKind == JsonValueKind.Object ? new(StringComparer.Ordinal) : null;
        internal List<KnownCaptureValue>? Items { get; } = value.ValueKind == JsonValueKind.Array ? [] : null;
        internal bool Open { get; set; }
    }
}
