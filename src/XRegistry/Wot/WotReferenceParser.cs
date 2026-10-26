// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Wot;

/// <summary>Normalizes compact WoT links and definition-reference objects without acquiring documents.</summary>
/// <remarks>This parser does not expand JSON-LD contexts or apply reference fragments.</remarks>
public static class WotReferenceParser
{
    /// <summary>Parses one link or object containing <c>tm:ref</c> or <c>td:ref</c>.</summary>
    /// <param name="reference">A link with rel and href, or an object with one definition reference.</param>
    /// <param name="baseUri">The document base used to resolve relative reference URIs.</param>
    /// <returns>A request whose floor syntax and term are checked during unpinned resolution.</returns>
    public static WotReferenceRequest Parse(JsonElement reference, Uri? baseUri = null)
    {
        if (reference.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A WoT reference must be a JSON object.", nameof(reference));
        }

        var hasTmRef = reference.TryGetProperty("tm:ref", out _);
        var hasTdRef = reference.TryGetProperty("td:ref", out _);
        if (hasTmRef && hasTdRef)
        {
            throw new ArgumentException("An object must contain only one definition reference.", nameof(reference));
        }

        var kind = hasTmRef ? "ref" : hasTdRef ? "td-ref" : ReadString(reference, "rel") switch
        {
            "tm:extends" => "extends",
            "tm:submodel" => "submodel",
            "type" => "type",
            "td:extends" => "td-extends",
            "td:subthing" => "td-subthing",
            _ => throw new ArgumentException("The link is not a supported WoT versioned reference.", nameof(reference))
        };
        var target = ReadString(reference, hasTmRef ? "tm:ref" : hasTdRef ? "td:ref" : "href");
        if (!Uri.TryCreate(target, UriKind.Absolute, out var absolute) ||
            !target.StartsWith(absolute.Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            if (baseUri is null || !baseUri.IsAbsoluteUri || !Uri.TryCreate(baseUri, target, out var resolved))
            {
                throw new ArgumentException("A relative reference requires an absolute document base URI.", nameof(baseUri));
            }

            target = resolved.AbsoluteUri;
        }

        if (hasTmRef || hasTdRef)
        {
            var fragment = target.IndexOf('#');
            if (fragment >= 0)
            {
                target = target[..fragment];
            }
        }

        var hasTmFloor = reference.TryGetProperty("tm:minVersion", out _);
        var hasTdFloor = reference.TryGetProperty("td:minVersion", out _);
        if (hasTmFloor && hasTdFloor)
        {
            throw new WotVersionException("invalid-floor");
        }

        var floorTerm = hasTmFloor ? "tm:minVersion" : hasTdFloor ? "td:minVersion" : null;
        return new(kind, target, floorTerm is null ? null : ReadFloor(reference, floorTerm), floorTerm);
    }

    private static string ReadFloor(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new WotVersionException("invalid-version");
        }

        return value.GetString()!;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"The reference member {name} must be a nonempty string.", nameof(element));
        }

        return value.GetString()!;
    }
}
