// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XRegistry;

internal sealed class ModelIncludes
{
    private readonly RegistryJson _source;
    private readonly RegistryModelCompilationOptions _options;
    private readonly Uri _sourceUri;
    private readonly Dictionary<Uri, RegistryJson> _documents = [];
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private int _nodes;
    private bool _changed;

    internal ModelIncludes(RegistryJson source, RegistryModelCompilationOptions options)
    {
        _source = source;
        _options = options;
        _sourceUri = options.SourceUri ?? new Uri("https://xregistry.invalid/__modelsource");
        _documents[_sourceUri] = source;
    }

    internal RegistryJson Expand()
    {
        _active.Add(_sourceUri.AbsoluteUri + "#");
        var result = ExpandValue(_source.RootElement, _sourceUri, "", 0);
        return _changed ? result.ToJson(_options.JsonLimits) : _source;
    }

    private JsonTree ExpandValue(JsonElement node, Uri origin, string path, int depth)
    {
        if (++_nodes > _options.MaxExpandedNodes)
        {
            throw Diagnostics.Error("model_expansion_limit", path, "The model exceeds its expansion work budget.");
        }

        if (depth > _options.JsonLimits.MaxDepth)
        {
            throw Diagnostics.Error("model_expansion_limit", path, "The expanded model exceeds its nesting budget.");
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            return new JsonTree(node.EnumerateArray().Select(value =>
                ExpandValue(value, origin, Diagnostics.At(path, (index++).ToString(CultureInfo.InvariantCulture)), depth + 1)).ToList());
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return new JsonTree(node);
        }

        var members = new Dictionary<string, JsonTree>(StringComparer.Ordinal);
        var single = node.TryGetProperty("$include", out var include);
        var multiple = node.TryGetProperty("$includes", out var includes);
        if (single && multiple)
        {
            throw Diagnostics.Error("model_error", path, "$include and $includes cannot coexist in one object.");
        }

        foreach (var property in node.EnumerateObject())
        {
            if (property.Name is not ("$include" or "$includes"))
            {
                members[property.Name] = ExpandValue(property.Value, origin, Diagnostics.At(path, property.Name), depth + 1);
            }
        }

        if (single || multiple)
        {
            _changed = true;
            if (multiple && includes.ValueKind != JsonValueKind.Array)
            {
                throw Diagnostics.Error("model_error", path + "/$includes", "$includes must be an array of references.");
            }

            var references = single ? [include] : includes.EnumerateArray().ToArray();
            for (var index = 0; index < references.Length; index++)
            {
                var at = single ? path + "/$include" :
                    Diagnostics.At(path + "/$includes", index.ToString(CultureInfo.InvariantCulture));
                if (references[index].ValueKind != JsonValueKind.String)
                {
                    throw Diagnostics.Error("model_error", at, "An include reference must be a string.");
                }

                var expanded = ExpandReference(references[index].GetString()!, origin, at, path, depth);
                foreach (var property in expanded.Members!)
                {
                    members.TryAdd(property.Key, property.Value);
                }
            }
        }

        return new JsonTree(members);
    }

    private JsonTree ExpandReference(string reference, Uri origin, string diagnosticPath, string destinationPath, int depth)
    {
        var hash = reference.IndexOf('#');
        var documentPart = hash < 0 ? reference : reference[..hash];
        var fragment = hash < 0 ? "" : DecodeFragment(reference[(hash + 1)..], diagnosticPath);
        if (fragment.Length > 0 && fragment[0] != '/')
        {
            throw Diagnostics.Error("invalid_json_pointer", diagnosticPath, "An include fragment must be an RFC6901 pointer, beginning with '/'.");
        }

        var uri = origin;
        if (documentPart.Length > 0)
        {
            if (!Uri.TryCreate(documentPart, UriKind.Absolute, out var absolute))
            {
                if (_options.SourceUri is null && origin == _sourceUri)
                {
                    throw Diagnostics.Error("model_base_uri_required", diagnosticPath, "Relative document includes require an explicit source URI.");
                }

                if (!Uri.TryCreate(origin, documentPart, out absolute))
                {
                    throw Diagnostics.Error("model_error", diagnosticPath, "The include document reference cannot be resolved.");
                }
            }

            uri = absolute;
        }

        var key = uri.AbsoluteUri + "#" + fragment;
        if (_active.Contains(key))
        {
            throw Diagnostics.Error("model_include_cycle", diagnosticPath, "The model contains a circular include chain.");
        }

        if (_active.Count > _options.MaxIncludeDepth)
        {
            throw Diagnostics.Error("model_expansion_limit", diagnosticPath, "The include chain exceeds its depth budget.");
        }

        if (!_documents.TryGetValue(uri, out var document))
        {
            if (_options.Resolver is null)
            {
                throw Diagnostics.Error("model_resolution_required", diagnosticPath, "An explicit resolver is required for this model document.");
            }

            if (_documents.Count - 1 >= _options.MaxIncludeDocuments)
            {
                throw Diagnostics.Error("model_expansion_limit", diagnosticPath, "The model exceeds its external document budget.");
            }

            document = _options.Resolver.Resolve(uri) ??
                throw Diagnostics.Error("model_resolution_failed", diagnosticPath, "The model resolver returned no document.");
            _documents[uri] = document;
        }

        var selected = Select(document.RootElement, fragment, diagnosticPath);
        if (selected.ValueKind != JsonValueKind.Object)
        {
            throw Diagnostics.Error("model_error", diagnosticPath, "An include must reference an object or map.");
        }

        _active.Add(key);
        try
        {
            return ExpandValue(selected, uri, destinationPath, depth);
        }
        finally
        {
            _active.Remove(key);
        }
    }

    internal static JsonElement Select(JsonElement node, string fragment, string path)
    {
        if (fragment.Length == 0)
        {
            return node;
        }

        foreach (var raw in fragment[1..].Split('/'))
        {
            var builder = new StringBuilder(raw.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '~')
                {
                    if (++i >= raw.Length || raw[i] is not ('0' or '1'))
                    {
                        throw Diagnostics.Error("invalid_json_pointer", path, "An include pointer contains an invalid tilde escape.");
                    }

                    builder.Append(raw[i] == '0' ? '~' : '/');
                }
                else
                {
                    builder.Append(raw[i]);
                }
            }

            var name = builder.ToString();
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var child))
            {
                node = child;
            }
            else if (node.ValueKind == JsonValueKind.Array && name.Length > 0 &&
                (name.Length == 1 || name[0] != '0') &&
                !name.AsSpan().ContainsAnyExcept("0123456789".AsSpan()) &&
                int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < node.GetArrayLength())
            {
                node = node[index];
            }
            else
            {
                throw Diagnostics.Error("model_reference_not_found", path, "The include pointer does not identify a value.");
            }
        }

        return node;
    }

    internal static string DecodeFragment(string text, string path)
    {
        var input = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[input.Length];
        var count = 0;
        for (var index = 0; index < input.Length; index++)
        {
            if (input[index] == '%')
            {
                if (index + 2 >= input.Length || Hex(input[index + 1]) < 0 || Hex(input[index + 2]) < 0)
                {
                    throw Diagnostics.Error("invalid_json_pointer", path, "Invalid percent escape in an include fragment.");
                }

                bytes[count++] = (byte)(Hex(input[index + 1]) * 16 + Hex(input[index + 2]));
                index += 2;
            }
            else
            {
                bytes[count++] = input[index];
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, 0, count);
        }
        catch (DecoderFallbackException exception)
        {
            throw new RegistryException(new("invalid_json_pointer", path, "An include fragment contains invalid UTF-8."), exception);
        }
    }

    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => -1
    };
}
