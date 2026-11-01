# Message property declarations

Message metadata describes another message; its literal property constraints are
not ordinary xRegistry attributes. `RegistryDomainRules.ValidateMessageMetadata`
checks the known declaration sections, and `CompleteMessageMetadata` returns
owned metadata with the domain's defaults applied to declarations which are
actually present. Neither operation resolves a base message or acquires a URI.

CloudEvents `envelopemetadata` is a flat object. The five AMQP declaration
sections (`properties`, `application-properties`, `message-annotations`,
`delivery-annotations`, `footer`) also contain property declaration objects.
SPEC-010 models these sections as opaque Core `any` values so Core attribute
deletion does not erase a literal `value: null`, and Core name syntax does not
change protocol-specific property names. Domain shape/type checks remain
mandatory; `any` is not a bypass for invalid known declarations.

The validator distinguishes omitted `value` from a present literal null.
Boolean, integer, number, binary, timestamp, URI, symbol and other declared
types require their actual JSON representation. CloudEvents Integer uses its
signed 32-bit range; values are never rounded or rewritten. URI property values
are absolute; `specurl` may be a relative reference. String controls and Unicode
noncharacters prohibited by CloudEvents are rejected. Fixed AMQP properties
retain their own types and widths, including unsigned message IDs and Group
sequence numbers. Base64 constraints are canonical byte representations.

Property `specurl` declarations use Core relative-or-absolute URI syntax even
when the containing declaration map is modeled as `any`. Malformed escapes,
backslashes, raw whitespace and invalid authority spellings reject at the
declaration's `specurl` path. Valid references retain their authored spelling;
validation does not fetch the specification or turn a relative reference into
an absolute URI. Inherited references keep the declaring Message's origin in
materialization provenance, independently of the derived Message's location.
These checks apply to CloudEvents, AMQP property sections and protocol headers.

Message `dataschemauri` also uses Core URI-reference syntax in the direct domain
API and explicitly Message-compatible models, including models that declare the
attribute as `any`. Empty and relative references retain their authored spelling;
absent or cleared references remain optional. Syntax validation does not fetch a
schema or establish that the reference selects a concrete schema object. That
selection still requires the chosen format's resolver and authorized source.

`ValidateMessageSchemaReference` checks that `dataschemaxid` identifies a local
Schema Resource in the effective model. When `dataschemauri` is also present,
its document portion must match the binding's explicit self-URL projection;
a fragment remains the schema format's object selector. The callback does not
acquire the target or require a dangling Resource to exist. Matching that
document identity alone does not prove that its fragment selects a schema object.

Domain completion applies string/false defaults where appropriate, mandatory
CloudEvents flags, fixed `specversion: "1.0"` and the documented current-time
sentinel for declared `time`. It does not invent absent declarations or replace
the sentinel with the clock during registry storage. Definition materialization
also retains it; creating an actual application message is the caller's separate
runtime operation, which supplies its clock and template values.

The duration syntax is the explicit ISO 8601/XML Schema lexical profile in the
corrected source, not a nonexistent RFC3339 duration grammar. Validation does
not narrow durations to `TimeSpan`. HTTP/MQTT/AMQP protocol range and header
rules remain separate from generic Core integer/string types.

The AMQP `header` section is an object of native scalar fields.
`durable` and `first-acquirer` are booleans, not property-declaration objects
with nested `type` or `value` fields. Direct domain validation and explicitly
Message-compatible models enforce these shapes even when `header` is modeled
as `any`; priority, time-to-live and delivery-count retain their protocol
integer bounds. Valid scalar values are stored without coercion.

MQTT `retain` is also a native boolean in both MQTT/3.1.1 and MQTT/5.0.
Domain validation rejects strings, numbers and declaration objects rather than
coercing them to a flag, including in compatible models that use `any` for this
field. Stored `true` and `false` remain distinct; a Core null patch clears the
optional field before validating the resulting Message metadata.

Native HTTP path, MQTT topic/response-topic, Kafka topic/key and NATS
subject/reply-to fields remain strings, including in compatible models that
declare them as `any`. Domain validation rejects non-string shapes and preserves
authored Level-1 templates. HTTP `query` is a map of string values, not an array
of declaration objects; empty maps and empty string values remain valid.
Query parameter names such as `description`, `type` and `required` do not
identify declaration metadata. Their values receive the same template checks as
every other query parameter.
Runtime substitution and agreement of repeated variables still belong to the
application that creates the message.

Kafka `partition` also retains its native integer representation when a
compatible model declares the field as `any`. Fractional numbers and non-number
JSON values reject before publication. Integral decimal and exponent spellings
remain valid under Core integer semantics; this check does not discover broker
partitions or establish that a selected partition exists.

Message and Message Group protocol selectors require a version for MQTT,
whose 3.1.1 and 5.0 declarations differ. Bare `MQTT` rejects rather than
bypassing both versions' checks. Versioned and custom selector spellings remain
unchanged. This Message rule does not change Endpoint selector admission.

Server completion calls the same pure domain implementation after Core
validation, only for explicitly Message-compatible Resources. Rejected writes
and patches publish no partial metadata or outbox changes. Other custom models
retain ordinary Core behavior. Opaque payload schemas and `any` literal values
are not interpreted as Message declaration objects.

HTTP/NATS headers, MQTT user properties and Kafka header map items now use the
same common declaration fields and defaults. Their opaque item boundaries retain
raw member presence before validation, while outer array/map rules remain Core
rules. Header names are required; native text values support string-valued
refinements rather than boolean/number/object-to-string coercion. Kafka explicitly
supports canonical base64 binary values and an `any` literal null byte value.
Duplicate array headers and exact order are preserved.

Content-type declarations for the same message representation compare media
types and parameter names without case sensitivity. The `charset` parameter's
value also compares without case sensitivity, as required by RFC 2046
section 4.1.2; quoted and unquoted spellings are compared after unquoting.
Other parameter values retain case-sensitive comparison, including multipart
boundaries. Comparison does not rewrite any stored declaration. Different
character sets or case-sensitive parameter values still reject, while structured
CloudEvents payload and envelope content types remain separate representations.
In structured mode, `envelopeoptions.format` declares the outer representation
and must agree with literal outer `datacontenttype` and protocol content-type
declarations. It does not constrain the nested payload's content type. Invalid
format syntax or conflicting outer declarations reject without changing stored
metadata or events; equivalent media-type spellings remain authored values.
CloudEvents `envelopeoptions` remains optional, but a present non-null value
must be an object. Its `mode` is `binary` or `structured`, and its `format` is
a string when present. Direct completion and compatible models enforce these
known shapes even when Core models them as `any`. Unknown option members remain
opaque; these constraints do not assign meanings to custom envelope options.

Local `basemessage` admission uses `ValidateMessageBaseReference` to check the
actual Message model type and Resource/Version path. It does not require a
target to exist, fetch it, or invoke the host's unrelated target policy.
Unmarked custom models keep their ordinary Core obligations. Consumers use
`MessageDefinitionMaterializer` for cycle-checked, bounded recursive resolution
through an explicitly authorized callback, with incomplete outcomes kept explicit.
