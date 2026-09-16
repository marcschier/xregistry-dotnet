# Endpoint templates and consumer materialization

`EndpointDefinition.ValidateAuthored` and `EndpointDefinition.Materialize` are
separate pure operations in `XRegistry.Models`. The former checks authoring shapes
and template syntax without bindings. The latter accepts explicit bindings,
returns owned metadata, and checks resolved consumer semantics. Neither contacts
a Registry, broker, authorization authority, or schema provider.

The normative source is the active Endpoint 1.0-rc4 text at
`tests\Conformance\Corrections\SPEC-012\endpoint\spec.md`
(SHA-256 `9cc6d33141691b0ea91b011ffa728e4d3c0c0edb50aeaf641725171cdf051fa8`).
Its Protocol Options section explicitly makes MUST/MUST NOT/REQUIRED rules
obligations of an evaluating client, **not mandatory passive-server checks**.
Only authored validation is installed in server storage validation, not
`Materialize` or its consumer-only protocol rules.

**Storage admission is integrated with SPEC-012.** Its six `protocoloptions`
definitions use an opaque Core `any` authoring boundary. The existing
`RegistryEngine` Endpoint marker dispatch invokes
`RegistryDomainRules.ValidateEndpointMetadata`, which now delegates authored
checks to this module. Core's ordinary URI, name, and enum rules remain unchanged.
The source correction was captured separately by the parent; this implementation
does not patch model sources, resources, manifests, locks, or the corpus.

## Authored storage validation

```csharp
using XRegistry;
using XRegistry.Models;

var authored = RegistryJson.Parse("""
    {"usage":["producer"],"protocol":"HTTP","protocoloptions":{
     "endpoints":[{"uri":"https://api.example/{tenant}/events"}],
     "query":{"{parameter}":"{tenant}"},"apikeyin":"{placement}"}}
    """);
EndpointDefinition.ValidateAuthored(authored);
// No bindings required; every authored value remains unchanged.
```

Authored validation requires `protocoloptions` to be an object when present.
Literal known names retain their declared JSON kinds: native booleans/numbers,
strings, arrays, and objects; string maps and arrays retain string members.
Quoted Boolean/Number placeholders are rejected, not coerced. A templated option
name is not guessed before bindings exist. Structural address, authorization, and
HTTP-header record names are literal; Map keys can contain expressions.

The same option tables, structural location rules, and Level-1 parser serve both
phases. The authored pass does not expand placeholders, check prospective key
collisions, demand variable values, or inject defaults. It checks expression
syntax without requiring literal URI text to be a valid resolved address.
String-enum membership, numeric integrality/ranges, required resolved address
members, HTTP token semantics, MQTT/NATS addressing, and Kafka role constraints
remain consumer checks. Thus an invalid publish topic or literal URI can remain
stored metadata while `Materialize` rejects it before consumer use.

Existing usage, selectors, envelope, group relationships, and common nonempty
authorization metadata rules remain in force. The engine activates these rules
only for the exact Endpoint `modelcompatiblewith` marker, including when the
Group has another name. Unmarked custom models and other domain markers do not
gain Endpoint restrictions. Marker adoption and invalid patches/batches are
validated before publication; rejected writes leave metadata, epochs, and outbox
state unchanged, including after persistence restart.

Common `envelopeoptions` object/CloudEvents mode checks also run at the standalone
authored interface and for explicitly marked custom models; they do not depend on
the packaged model's enum declarations. Known authorization string members cannot
use an opaque JSON `null` as an omitted field: omit the member instead. Extension
members keep their own JSON kinds, and unresolved string placeholders remain valid.

`ValidateAuthored` reports precise authored diagnostic codes and paths. The
server-facing domain adapter maps these to the existing `invalid_attribute`
problem contract while preserving the original diagnostic as its inner cause.
Neither phase forwards incoming credentials or performs acquisition. Existing
Core target obligations outside the opaque option boundary, such as
`messagegroups` host-validator policy, are unchanged.

## Interface and substitution

```csharp
using XRegistry;
using XRegistry.Models;

var authored = RegistryJson.Parse("""
    {"usage":["producer"],"protocol":"HTTP",
     "protocoloptions":{"endpoints":[{"uri":"https://api.example/{tenant}/events"}]}}
    """);
var endpoint = EndpointDefinition.Materialize(authored,
    RegistryJson.Parse("""{"tenant":"north/west"}"""));
var uri = endpoint.GetProtocolOption("endpoints")[0].GetProperty("uri").GetString();
// https://api.example/north%2Fwest/events
```

`Authored` retains the original values, including templates and exact number
tokens. `Resolved` substitutes only within `protocoloptions`; it does not rewrite
selectors, channel names, message-group references, inlined Message definitions,
or schemas. Both values own their JSON independently of caller buffers and
disposable documents. Resolved JSON may use different whitespace/escape spelling;
string values, booleans, nulls, and exact numeric tokens are preserved.

Bindings are a JSON object whose referenced values must be strings. Names use
[RFC 6570](https://www.rfc-editor.org/rfc/rfc6570) `varname` syntax and ordinal,
case-sensitive lookup. Dotted names and percent triplets in names are valid;
triplets in a **name are not decoded**. Endpoint does not impose Message's
narrower symbol-name rule.

| Input or location | Behavior |
|---|---|
| `{name}` | One Level-1 simple expansion; repeated occurrences use the same binding. |
| `{+name}`, `{#name}`, `{name,other}`, `{name:3}`, `{name*}` | Explicit `invalid_endpoint_template` failure, not a partial expansion. |
| Missing or null binding | `undefined_template_variable`, never an implicit empty string. |
| Binding `""` | Defined empty string; the resolved option still must satisfy its own constraints. |
| Numeric, boolean, list, or object binding | Rejected; this interface does not inject typed JSON or implement Level-4 composites. |
| Protocol-option strings, string array items, map keys/values | Expanded recursively. Map-key collisions fail instead of overwriting. |
| Endpoint-address, authorization, and HTTP-header record member names | Literal structural names, not templated map keys. Their string **values** can contain templates. |
| A boolean/number option written as `"{value}"` | Remains a string and fails resolved type checking; no coercion. |

Every variable value uses RFC simple-expansion UTF-8 percent encoding, including
in non-URI options. For example, `Hello World!` becomes `Hello%20World%21`,
`a/b` becomes `a%2Fb`, and an already escaped binding `%2F` becomes `%252F`.
This is deliberately **not regular string replacement**. Literal text in plain
strings is retained. In templated URI-valued locations, Unicode URI-template
literals are encoded and existing literal percent triplets remain unchanged;
`%{hex}` cannot assemble a percent triplet from an expression. Bindings are not
expanded again. Malformed braces in templateable locations fail explicitly.

`GetProtocolOption` returns the specified value or a normative default:
`deployed=true`; HTTP `POST`/`header`/`basic`; AMQP `durable=false` and
`distribution-mode=move`; MQTT `qos=0`, `retain=false`, and 3.1.1
`cleansession=true`; Kafka `acks=1`. It returns a JSON `Undefined` element for an
absent option without a default. It does not inject defaults into either JSON
snapshot or invent serializer defaults not specified by the Endpoint tables.
`Protocol` recognizes case-insensitive predefined names and the AMQP/MQTT
shorthands. `IsDeployed` interprets metadata, not reachability.
Use `RegistryNumber` for semantic numeric access: `2e0` is an exact integer even
though `JsonElement.GetInt32()` does not accept every legal integer spelling.

## Resolved consumer checks

All predefined option tables have native scalar/container checks and
case-sensitive enum checks, including required address-record and HTTP-header
members. Exact integer arithmetic checks MQTT QoS/retain handling, MQTT 5 session
expiry, Kafka acknowledgments, and unsigned AMQP timeout without floating-point
rounding. Unknown extension options remain metadata with an explicit deferred
contract; a descriptive `-` role-table cell is not turned into a prohibition.

| Protocol | Additional resolved checks |
|---|---|
| HTTP | HTTP/HTTPS network URIs, method/header tokens, duplicate header retention, field-value controls, carrier enums, and declared CloudEvents format versus HTTP Content-Type. |
| AMQP[/1.0] | AMQP/AMQPS URIs, option enums, native strings/booleans/maps/arrays, and unsigned timeout. `node` is a string, not an invented URI-only field. Container-specific node/terminus resolution remains deferred; explicit `node` overrides the URI path without bypassing URI checks. |
| MQTT[/5.0], MQTT/3.1.1 | MQTT/MQTTS paths must be concrete topics after one URI percent decoding. TCP/SSL/WSS forms must have **no path**, including no trailing `/`. Topic/filter exclusion, wildcard placement, Will references, shared-group dependencies, and the 65,535-byte MQTT string limit are checked. |
| KAFKA | Nonempty `bootstrap.servers`, one host and explicit port per value, supported listener forms, required consumer group for `consumer`, forbidden group for `subscriber`, and consumer-only offset/autocommit settings. Custom listener names require an explicit security mapping. |
| NATS | NATS/TLS/WS URIs with an explicit port; concrete subjects, legal filter tokens, subject/filter exclusion, and queue-group dependency. |

URI-valued network addresses use strict lexical URI checks without rewriting
authored URI spelling. The connection-oriented safety profile rejects userinfo,
fragments, malformed escapes, non-UTF-8 escaped path/query text, decoded controls,
and explicit ports outside `1..65535`. Network-address parsing also requires a
host supported by `System.Uri`; it is not a universal parser for every future URI
address form. Relative nonempty authorization URI references remain permitted.
These are consumer checks, not newly imposed storage rules.

Authorization alternatives remain configuration metadata. `type` and `mechanism`
are not invented as universally required fields; a supplied mechanism requires
`type=SASL`. Empty alternatives and invalid supplied fields fail. Provider-specific
selection/discovery sufficiency is explicitly deferred. The module rejects
recognized credential properties, URI userinfo/token queries, HTTP
Authorization/Proxy-Authorization/cookie headers, and configured credential
carriers. Diagnostics do not echo rejected values. This is not a general secret
detector for arbitrary extensions: callers must not put secrets in metadata or
bindings and must supply runtime credentials separately.

There is no incoming-header/credential forwarding interface, DNS lookup, network
fetch, token acquisition, subscription creation, or broker/auth-secret provisioning.
The caller still owns destination authorization, TLS trust, transport configuration,
runtime state, and actual protocol operations.

## MQTT shared subscriptions

```csharp
var authored = RegistryJson.Parse("""
    {"usage":["subscriber","consumer"],"protocol":"MQTT",
     "protocoloptions":{"endpoints":[{"uri":"mqtts://broker.example"}],
      "topicfilter":"{tenant}/orders/+","sharedsubscriptiongroup":"{group}"}}
    """);
var endpoint = EndpointDefinition.Materialize(authored,
    RegistryJson.Parse("""{"tenant":"north/west","group":"workers"}"""));
var filter = endpoint.MqttSubscriptionFilter;
// $share/workers/north%2Fwest/orders/+
```

`MqttSubscriptionFilter` combines the resolved option **literals** exactly as the
draft requires. It does not perform a second URI-template expansion or send a
SUBSCRIBE packet. Author wildcard tokens literally when they are intended:
a binding containing `+` expands to `%2B`, not to a wildcard in a plain option.

## Explicit Message selection

```csharp
var authored = RegistryJson.Parse("""
    {"usage":["producer"],"protocol":"HTTP","messagegroups":["/messagegroups/orders"]}
    """);
var supplied = RegistryJson.Parse("""
    {"/messagegroups/orders":{"messages":{"order.created":{"messageid":"order.created","description":"An order was created"}}}}
    """);
var endpoint = EndpointDefinition.Materialize(authored,
    options: new EndpointTemplateOptions { MessageGroups = supplied });
var message = endpoint.SelectMessage("order.created", "/messagegroups/orders");
```

`MessageGroups` is keyed by the exact declared reference, without implicit URI
normalization or credential propagation. Same-Registry references must be
messagegroups Group XIDs; external references must be absolute URIs. Only declared
Groups participate. Repeated identical references are collected once.

`Messages` contains immutable candidates, not inherited/materialized Message
contracts. Explicit selectors are checked using the existing domain group rules,
including Endpoint envelope refinement and a referenced Group's protocol for an
otherwise unbound Message. Message inheritance/borrowing and runtime binding/schema
evaluation are explicitly deferred rather than fetched or invented.

Duplicate IDs across collections are permitted by default. `SelectMessage(id)`
requires a complete, unambiguous combined set. Supply the exact Group reference
to disambiguate, or `""` to select the inline collection. Missing, ambiguous, and
incomplete selections have distinct errors. Set `RejectDuplicateMessageIds=true`
only when that optional uniqueness policy is wanted. No runtime CloudEvents
discriminator or JSON/XML serialization discriminator is guessed.

Missing Groups, omitted referenced `messages` collections, and count-mismatched
collections are incomplete, not empty success results. Explicit `messages: {}`
is a known empty collection. Referenced-message diagnostic paths use
`/suppliedmessagegroups/<escaped-reference>/messages/<escaped-id>`.

## Bounds, cancellation, and deferred work

All limits are inclusive and configurable with `EndpointTemplateOptions`.
For authored validation, variable count means distinct referenced names,
occurrence count includes repeats, and the string budget applies to authored
templateable values/keys. There are no substitution bytes, group acquisition, or
derived consumer results in that phase. Engine marker dispatch uses the default
Endpoint authoring budgets.

| Budget | Default |
|---|---|
| Each input JSON value and resolved JSON | 4 MiB, 100,000 values, depth 64 (hard ceiling 256) |
| Exact numeric token / exponent | 1,024 characters / absolute explicit exponent 10,000 |
| Supplied variables / expansion occurrences | 256 / 4,096 |
| One templateable string/key or derived MQTT filter | 64 KiB UTF-8 |
| Aggregate substitution bytes, counting repeated uses | 1 MiB |
| Supplied Groups and declared references, separately | 256 each |
| Combined Message candidates / aggregate candidate bytes | 4,096 / 4 MiB |
| Deferred obligations / their aggregate UTF-8 text | 1,024 / 64 KiB |

Budgets are checked before results are exposed; key collisions, undefined
variables, malformed metadata, and over-budget expansions never return partial
metadata. Input/option argument errors use argument exceptions, domain failures
use `RegistryException` with a code/path, and cancellation uses
`OperationCanceledException`. Materialization and selection honor cancellation.

`DeferredChecks` reports absent addressing, unknown protocol/extension contracts,
authorization configuration, AMQP node resolution, runtime envelope content type,
and unresolved Message collections/definitions as applicable. An empty list does
not certify a live, authorized, schema-valid, or operational endpoint. This is a
bounded metadata feature, not closure of every Endpoint clause or a release claim.

The consumer examples are executable public-seam cases in
`tests\XRegistry.Models.Tests\EndpointTemplateExamplesTests.cs`. Authored API
examples are exercised by `EndpointAuthoredValidationTests`; real storage,
roundtrip, rollback, marker isolation, restart and unchanged Core controls are
exercised by `tests\XRegistry.Server.Tests\EndpointAuthoredStorageTests.cs`:

```powershell
dotnet test --project tests\XRegistry.Models.Tests\XRegistry.Models.Tests.csproj -c Release -f net8.0 --artifacts-path artifacts\endpoint-template-materialization --treenode-filter '/*/*/EndpointTemplateExamplesTests/*' --minimum-expected-tests 3 --zero-tests-policy strict --no-ansi
dotnet test --project tests\XRegistry.Models.Tests\XRegistry.Models.Tests.csproj -c Release -f net10.0 --artifacts-path artifacts\endpoint-template-materialization --treenode-filter '/*/*/EndpointTemplateExamplesTests/*' --minimum-expected-tests 3 --zero-tests-policy strict --no-ansi
```
