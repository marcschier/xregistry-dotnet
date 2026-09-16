// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using static XRegistry.Models.EndpointTemplateExpansion;

namespace XRegistry.Models;

internal static partial class EndpointDefinitionSemantics
{
    private static void ValidateAuthorizationShape(JsonElement options, CancellationToken cancellationToken)
    {
        if (!options.TryGetProperty("authorization", out var authorization))
        {
            return;
        }
        RequireKind(authorization, JsonValueKind.Array, "/protocoloptions/authorization");
        var index = 0;
        foreach (var entry in authorization.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = At("/protocoloptions/authorization", (index++).ToString(CultureInfo.InvariantCulture));
            RequireKind(entry, JsonValueKind.Object, path);
            foreach (var property in entry.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (property.Name is "type" or "mechanism" or "resourceuri" or "authorityuri")
                {
                    RequireKind(property.Value, JsonValueKind.String, At(path, property.Name));
                }
            }
        }
    }

    private static void ValidateAuthorization(JsonElement endpoint, JsonElement options, EndpointDefinitionEvaluation evaluation)
    {
        if (options.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var option in options.EnumerateObject())
        {
            evaluation.CancellationToken.ThrowIfCancellationRequested();
            RejectCredentialName(option.Name, At(OptionsPath, option.Name));
        }
        if (options.TryGetProperty("authorization", out var authorization))
        {
            var index = 0;
            foreach (var entry in authorization.EnumerateArray())
            {
                evaluation.CancellationToken.ThrowIfCancellationRequested();
                var path = At("/protocoloptions/authorization", (index++).ToString(CultureInfo.InvariantCulture));
                if (!entry.EnumerateObject().Any())
                {
                    throw Invalid(path, "An authorization alternative must supply selection and discovery metadata.");
                }
                RejectNestedCredentials(entry, path, evaluation.CancellationToken);
                var type = Text(entry, "type", path, nonempty: true);
                if (Text(entry, "mechanism", path, nonempty: true) is not null && type != "SASL")
                {
                    throw Invalid(At(path, "mechanism"), "An authorization mechanism is only defined for type SASL.");
                }
                foreach (var name in new[] { "resourceuri", "authorityuri" })
                {
                    if (Text(entry, name, path, nonempty: true) is { } reference)
                    {
                        var uriPath = At(path, name);
                        ValidateReference(reference, uriPath);
                        if (ReferenceHasUserInfo(reference))
                        {
                            throw Credentials(uriPath);
                        }
                        var query = reference.IndexOf('?', StringComparison.Ordinal);
                        if (query >= 0)
                        {
                            var end = reference.IndexOf('#', query);
                            RejectCredentialQuery(reference[(query + 1)..(end < 0 ? reference.Length : end)],
                                uriPath, evaluation.CancellationToken);
                        }
                    }
                }
                evaluation.Defer("authorization_configuration", path,
                    "The consumer must select an authorization alternative, verify provider-specific metadata, and supply credentials separately.");
            }
        }
        if (Protocol(endpoint) != "HTTP")
        {
            return;
        }
        if (options.TryGetProperty("headers", out var headers))
        {
            var index = 0;
            foreach (var header in headers.EnumerateArray())
            {
                evaluation.CancellationToken.ThrowIfCancellationRequested();
                var path = At("/protocoloptions/headers", (index++).ToString(CultureInfo.InvariantCulture));
                var name = header.GetProperty("name").GetString()!;
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
                    IsCredentialName(name) || MatchesCarrier(endpoint, options, name, header: true))
                {
                    throw Credentials(At(path, "value"));
                }
            }
        }
        if (options.TryGetProperty("query", out var queryOptions))
        {
            foreach (var query in queryOptions.EnumerateObject())
            {
                evaluation.CancellationToken.ThrowIfCancellationRequested();
                if (IsCredentialName(query.Name) || MatchesCarrier(endpoint, options, query.Name, header: false))
                {
                    throw Credentials(At("/protocoloptions/query", query.Name));
                }
            }
        }
        if (options.TryGetProperty("endpoints", out var endpoints))
        {
            var index = 0;
            foreach (var address in endpoints.EnumerateArray())
            {
                var value = address.GetProperty("uri").GetString()!;
                var path = At(At("/protocoloptions/endpoints", (index++).ToString(CultureInfo.InvariantCulture)), "uri");
                var query = value.IndexOf('?', StringComparison.Ordinal);
                if (query >= 0)
                {
                    foreach (var part in value[(query + 1)..].Split('&'))
                    {
                        evaluation.CancellationToken.ThrowIfCancellationRequested();
                        var end = part.IndexOf('=', StringComparison.Ordinal);
                        var name = DecodeUriText(end < 0 ? part : part[..end], path, evaluation.CancellationToken);
                        if (MatchesCarrier(endpoint, options, name, header: false))
                        {
                            throw Credentials(path);
                        }
                    }
                }
            }
        }
    }

    private static bool MatchesCarrier(JsonElement endpoint, JsonElement options, string name, bool header)
    {
        var location = GetOption(endpoint, "apikeyin").GetString();
        var comparison = header ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Text(options, "apikeyname", OptionsPath) is { } key && string.Equals(name, key, comparison) &&
            (header ? location == "header" : location == "query"))
        {
            return true;
        }
        if (header || GetOption(endpoint, "plainscheme").GetString() is not ("form" or "query"))
        {
            return false;
        }
        return string.Equals(name, Text(options, "plainusernamefield", OptionsPath), StringComparison.Ordinal) ||
            string.Equals(name, Text(options, "plainpasswordfield", OptionsPath), StringComparison.Ordinal);
    }

    private static void RejectCredentialQuery(string query, string path, CancellationToken cancellationToken)
    {
        foreach (var part in query.Split('&'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = part.IndexOf('=', StringComparison.Ordinal);
            var name = DecodeUriText(end < 0 ? part : part[..end], path, cancellationToken);
            RejectCredentialName(name, path);
        }
    }

    private static void RejectNestedCredentials(JsonElement value, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var childPath = At(path, property.Name);
                RejectCredentialName(property.Name, childPath);
                RejectNestedCredentials(property.Value, childPath, cancellationToken);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                RejectNestedCredentials(item, At(path, (index++).ToString(CultureInfo.InvariantCulture)), cancellationToken);
            }
        }
    }

    private static bool IsCredentialName(string name) => name.ToUpperInvariant() is
        "USERNAME" or "PASSWORD" or "PASSWD" or "CREDENTIAL" or "CREDENTIALS" or "SECRET" or
        "CLIENTSECRET" or "CLIENT_SECRET" or "CLIENT-SECRET" or "TOKEN" or "ACCESS_TOKEN" or "REFRESH_TOKEN" or "ID_TOKEN" or
        "APIKEY" or "API_KEY" or "API-KEY" or "X-API-KEY" or "X-AUTH-TOKEN" or "SASL.JAAS.CONFIG";

    private static void RejectCredentialName(string name, string path)
    {
        if (IsCredentialName(name))
        {
            throw Credentials(path);
        }
    }

    private static RegistryException Credentials(string path) =>
        Error("endpoint_credentials", path, "Credential configuration and runtime credential carriers must be supplied outside Endpoint metadata.");

    private static void ValidateEnvelopeContentType(JsonElement endpoint, JsonElement options, EndpointDefinitionEvaluation evaluation)
    {
        if (!endpoint.TryGetProperty("envelope", out var envelope) ||
            !string.Equals(envelope.GetString(), "CloudEvents/1.0", StringComparison.OrdinalIgnoreCase) ||
            !endpoint.TryGetProperty("envelopeoptions", out var envelopeOptions) ||
            !envelopeOptions.TryGetProperty("format", out var format))
        {
            return;
        }
        var expected = MediaTypeHeaderValue.Parse(format.GetString()!);
        if (Protocol(endpoint) == "HTTP" && options.ValueKind == JsonValueKind.Object &&
            options.TryGetProperty("headers", out var headers))
        {
            var index = 0;
            foreach (var header in headers.EnumerateArray())
            {
                evaluation.CancellationToken.ThrowIfCancellationRequested();
                var path = At(At("/protocoloptions/headers", (index++).ToString(CultureInfo.InvariantCulture)), "value");
                if (string.Equals(header.GetProperty("name").GetString(), "Content-Type", StringComparison.OrdinalIgnoreCase) &&
                    (!MediaTypeHeaderValue.TryParse(header.GetProperty("value").GetString(), out var actual) || !expected.Equals(actual)))
                {
                    throw Invalid(path, "The declared HTTP Content-Type must agree with the Endpoint envelope format.");
                }
            }
        }
        evaluation.Defer("envelope_content_type", "/envelopeoptions/format",
            "The actual runtime message content type must match this declared structured-envelope format.");
    }
}
