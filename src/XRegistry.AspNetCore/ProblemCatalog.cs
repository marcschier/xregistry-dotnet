// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;

namespace XRegistry.AspNetCore;

internal sealed record ProblemDefinition(int Status, string Document, string Title, bool UsesRequestPath = false)
{
    internal string Type(string code) => "https://github.com/xregistry/spec/blob/main/core/" + Document + "#" + code;
}

internal static class ProblemCatalog
{
    private static readonly FrozenDictionary<string, ProblemDefinition> s_definitions =
        new Dictionary<string, ProblemDefinition>(StringComparer.Ordinal)
        {
            ["action_not_supported"] = Core(405, "The specified action (<action>) is not supported for: <subject>."),
            ["ancestor_circular_reference"] = Core(400, "For \"<subject>\", the request would create a circular list of ancestors: <list>."),
            ["bad_defaultversionid"] = Core(400, "For \"<subject>\", an error was found in the \"defaultversionid\" value specified (<value>): <error_detail>."),
            ["bad_details"] = Core(400, "Use of \"$details\" in this context is not allowed: <subject>."),
            ["bad_filter"] = Core(400, "For \"<subject>\", an error was found in \"filter\" value (<value>): <error_detail>."),
            ["bad_flag"] = Core(400, "The specified flag (<flag>) is not allowed in this context: <subject>."),
            ["bad_ignore"] = Core(400, "For \"<subject>\", an error was found in \"ignore\" value (<value>): <error_detail>."),
            ["bad_inline"] = Core(400, "For \"<subject>\", an error was found in \"inline\" value (<value>): <error_detail>."),
            ["bad_request"] = Core(400, "<error_detail>."),
            ["bad_sort"] = Core(400, "For \"<subject>\", an error was found in \"sort\" value (<value>): <error_detail>."),
            ["cannot_doc_xref"] = Core(400, "Retrieving the document view of a Version for \"<subject>\" is not allowed because it uses \"xref\"."),
            ["capability_error"] = Core(400, "There was an error in the capabilities provided: <error_detail>."),
            ["capability_missing_value"] = Core(400, "The \"<name>\" capability needs to contain \"<value>\"."),
            ["capability_unknown"] = Core(400, "Unknown capability specified: <field>."),
            ["capability_value"] = Core(400, "Invalid value (<value>) specified for capability \"<field>\". Allowable values include: <list>."),
            ["capability_wildcard"] = Core(400, "When \"<field>\" includes a value of \"*\" then no other values are allowed."),
            ["compatibility_unknown"] = Core(400, "The compatibility value (<compat>) on Resource \"<subject>\" is not supported for format \"<format>\"."),
            ["compatibility_violation"] = Core(400, "The request would cause one or more Versions of \"<subject>\" to violate its compatibility rule (<compat>)."),
            ["constraint_failure"] = Core(400, "The request would result in one or more Versions of \"<subject>\" not being compliant with its owning Group's \"<kind>\" constraint for attribute \"<path>\"."),
            ["data_retrieval_error"] = Core(500, "The server was unable to retrieve all of the requested data."),
            ["defaultversionid_request"] = Core(400, "Processing \"<subject>\", the \"defaultversionid\" attribute is not allowed to be \"request\" since a Version wasn't processed."),
            ["extra_xref_attribute"] = Core(400, "Attribute \"<name>\" is not allowed to be present since the \"<singular>\" (<subject>) uses \"xref\"."),
            ["format_external"] = Core(400, "Version \"<subject>\" references a document stored outside of the Registry, therefore no validation was performed."),
            ["format_unknown"] = Core(400, "Version \"<subject>\" has a \"format\" value (<format>) that it not supported."),
            ["format_violation"] = Core(400, "The request would cause Version \"<subject>\" to be non-compliant with its \"format\" (<format>)."),
            ["groups_only"] = Core(400, "Attribute \"<name>\" is invalid. Only Group types are allowed to be specified on this request: <subject>."),
            ["hasdocument_violation"] = Core(400, "The request would cause Version \"<subject>\" to be non-compliant. The model definition of \"<plural>\" has \"hasdocument\" set to \"false\" but this Version has document content."),
            ["inline_noninlineable"] = Core(400, "Attempting to inline a non-inlineable attribute (<name>) on: <subject>."),
            ["invalid_attribute"] = Core(400, "The attribute \"<name>\" for \"<subject>\" is not valid: <error_detail>."),
            ["malformed_id"] = Core(400, "For \"<subject>\", the specified ID value (<id>) is malformed: <error_detail>."),
            ["malformed_xid"] = Core(400, "For \"<subject>\", the specified XID value (<xid>) is malformed: <error_detail>."),
            ["malformed_xref"] = Core(400, "For \"<subject>\", the specified xref value (<xref>) is malformed: <error_detail>."),
            ["mismatched_epoch"] = Core(400, "The specified epoch value (<bad_epoch>) for \"<subject>\" does not match its current value (<epoch>)."),
            ["mismatched_id"] = Core(400, "The specified \"<singular>id\" value (<invalid_id>) for \"<subject>\" needs to be \"<expected_id>\"."),
            ["mismatched_version_attribute"] = Core(400, "The request would cause the \"<name>\" attribute across the Versions of \"<subject>\" to be different."),
            ["misplaced_epoch"] = Core(400, "The specified \"epoch\" value for \"<subject>\" needs to be within a \"meta\" entity."),
            ["model_compliance_error"] = Core(400, "The model provided would cause one or more entities in the Registry to become non-compliant."),
            ["model_error"] = Core(400, "There was an error in the model definition provided: <error_detail>."),
            ["model_required_true"] = Core(400, "Model attribute \"<name>\" needs to have a \"required\" value of \"true\" since a default value is provided."),
            ["model_scalar_default"] = Core(400, "Model attribute \"<name>\" is not allowed to have a default value since it is not a scalar."),
            ["multiple_roots"] = Core(400, "The operation would result in multiple root Versions for \"<subject>\", which is not allowed for \"<plural>\"."),
            ["not_available"] = Core(400, "The requested data (<subject>) is not available."),
            ["not_found"] = Core(404, "The targeted entity (<subject>) cannot be found."),
            ["one_resource"] = Core(400, "Only one attribute from \"<list>\" can be present at a time for: <subject>."),
            ["parsing_data"] = Core(400, "There was an error parsing \"<subject>\": <error_detail>."),
            ["readonly"] = Core(400, "Updating a read-only entity (<subject>) is not allowed."),
            ["required_attribute_missing"] = Core(400, "One or more mandatory attributes for \"<subject>\" are missing: <list>."),
            ["resources_only"] = Core(400, "Attribute \"<name>\" is invalid. Only Resource types are allowed to be specified on this request: <subject>."),
            ["server_busy"] = Core(503, "Due to excessive requests, the server could not complete \"<subject>\", please try again later."),
            ["server_error"] = Core(500, "An unexpected error occurred, please try again later."),
            ["setdefaultversionsticky_false"] = Core(400, "For \"<subject>\", setting \"defaultversionsticky\" to \"true\" is not allowed since \"maxversions\" is \"1\"."),
            ["sort_noncollection"] = Core(400, "Can't sort on a non-collection result set. Query path: <subject>."),
            ["too_large"] = Core(406, "For \"<subject>\", the size of the response is too large to return in a single response."),
            ["too_many_versions"] = Core(400, "For \"<subject>\", when the \"setdefaultversionid\" flag is set to \"request\", only one Version is allowed to be specified in the request message."),
            ["unknown_attribute"] = Core(400, "An unknown attribute (<name>) was specified for \"<subject>\"."),
            ["unknown_group_type"] = Core(400, "An unknown Group type (<name>) was specified in \"<subject>\"."),
            ["unknown_id"] = Core(400, "While processing \"<subject>\", the \"<singular>\" with a \"<singular>id\" value of \"<id>\" cannot be found."),
            ["unknown_resource_type"] = Core(400, "An unknown Resource type (<name>) was specified for Group type \"<group>\"."),
            ["unsupported_specversion"] = Core(400, "The specified \"specversion\" value (<specversion>) is not supported. Supported versions: <list>."),
            ["versionid_not_allowed"] = Core(400, "While creating a new Version for \"<subject>\", a \"versionid\" was specified but the \"setversionid\" model aspect for entities of type \"<plural>\" is \"false\"."),
            ["api_not_found"] = Http(404, "The specified API is not supported: <subject>."),
            ["details_required"] = Http(405, "$details suffix is needed when using PATCH for the entity: <subject>.", usesRequestPath: false),
            ["extra_xregistry_header"] = Http(400, "For \"<subject>\", xRegistry HTTP header \"<name>\" is not allowed on this request: <error_detail>."),
            ["header_error"] = Http(400, "For \"<subject>\", there was an error processing HTTP header \"<name>\": <error_detail>."),
            ["missing_body"] = Http(400, "For \"<subject>\", the request is missing an HTTP body - try '{}'."),
            ["missing_versions"] = Http(400, "For \"<subject>\", at least one Version needs to be included in the request.")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static ProblemDefinition? Find(string code) => s_definitions.GetValueOrDefault(code);

    internal static string Format(ProblemDefinition definition, string subject, IReadOnlyDictionary<string, string> arguments)
    {
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < definition.Title.Length;)
        {
            var start = definition.Title.IndexOf('<', index);
            if (start < 0)
            {
                result.Append(definition.Title.AsSpan(index));
                break;
            }

            result.Append(definition.Title.AsSpan(index, start - index));
            var end = definition.Title.IndexOf('>', start);
            var name = definition.Title[(start + 1)..end];
            result.Append(name == "subject" ? subject : arguments.TryGetValue(name, out var value) ? value :
                throw new InvalidOperationException("A standardized error is missing a required argument: " + name));
            index = end + 1;
        }

        return result.ToString();
    }

    internal static IReadOnlyList<string> ArgumentNames(ProblemDefinition definition)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var start = definition.Title.IndexOf('<'); start >= 0;)
        {
            var end = definition.Title.IndexOf('>', start);
            var name = definition.Title[(start + 1)..end];
            if (name != "subject")
            {
                names.Add(name);
            }

            start = definition.Title.IndexOf('<', end + 1);
        }

        return names.ToArray();
    }

    private static ProblemDefinition Core(int status, string title) => new(status, "spec.md", title);
    private static ProblemDefinition Http(int status, string title, bool usesRequestPath = true) =>
        new(status, "http.md", title, usesRequestPath);
}
