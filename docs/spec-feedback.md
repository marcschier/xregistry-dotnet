# Implementation feedback

The original baseline remains byte-identical under `tests/Conformance/Sources`.
Explicit successor artifacts and their before/after hashes are recorded in
`eng/specification/corrections.json` and `tests/Conformance/Corrections`.
These changes were also applied, without committing or pushing, to the current
`xregistry-spec` branch, whose HEAD had advanced to
`e0325126cc1d3d43c3d89323867f6b7d7e40761c` before correction.

## SPEC-001: built-in model sources must compile under the core model language

Compiling all seven packaged model sources exposed two specification artifacts
that could not satisfy the core model language:

- `cloudevents/model.json` used fragments such as `#groups`, while
  `core/model.md` requires RFC6901 JSON Pointers. They are now `#/groups`.
- Endpoint `usage` applied scalar `enum` to an array and omitted the domain's
  required flag. The model now uses a required string array. Allowed role
  members and protocol-dependent combinations remain unchanged normative
  Endpoint rules; `RegistryDomainRules.ValidateEndpointUsage` enforces them
  without pretending that the core array model has a scalar enum aspect.

Regenerating the affected schemas then exposed a directly related tool defect:
`schema-generator.py` only expanded `$include`, used the wrong precedence for
local values, and did not retain each nested document's base. Its bounded local
resolver now implements ordered `$includes`, local/earlier precedence, correct
nested bases, JSON Pointer validation and cycle rejection without mutating the
supplied source or silently performing network acquisition.

Endpoint and CloudEvents JSON Schema, JSON Structure, Avro and OpenAPI
projections were regenerated. These remain structural projections, not complete
oracles for procedural endpoint-role rules.

**Evidence:** the pre-fix .NET model compilation cases failed for Endpoint and
CloudEvents; the independent Python fragment/required-field cases also failed.
After correction, all seven sources compile through an explicitly packaged
resolver, and all 29 directly affected generator/regression tests pass. The
original failing source remains retained; neither the core enum rules nor the
JSON Pointer parser was weakened to accept it.

**Interop impact:** generic rc4 HTTP peer behavior was not changed. Consumers
that incorrectly treated `#groups` as a JSON Pointer or an array `enum` as a
core scalar aspect should use the corrected artifacts. Domain role constraints
still require procedural validation.

## SPEC-002: calendar-valid CloudEvents timestamps

Eleven CloudEvents examples used `2024-04-31T12:00:00Z` as a modification
timestamp after an April 30 creation time. Both the .NET timestamp validator
and an independent Python calendar check rejected that nonexistent date.
The examples now use May 1, preserving the intended next-day ordering.

`test_cloudevents_created_and_modified_examples_are_calendar_valid` checks all
eleven pairs and their ordering. No protocol rule or timestamp validation was
relaxed, and no OPC UA file was changed.

## SPEC-003: HTTP discovery must use the core array shape

`core/http.md` sketched `registries` using object braces around URL strings,
contradicting both discovery sections in `core/spec.md`. Replacing the sketch's
repetition metavariable with one actual URL reproduced a Python JSON parser
failure. The HTTP example now uses `["URL", *]`, preserving the Core contract.
`test_http_discovery_uses_the_core_array_shape` verifies the corrected concrete
shape; the .NET discovery tests independently require an array and reject an
object, invalid entries and count-budget overflow.

The client investigation also corrected a planning assumption: Registry-based
discovery uses Registry-relative `.xregistry`, while the separately specified
Host-based mechanism uses origin-relative `/.well-known/xregistry`. The latter
is valid; it must not be substituted for the Registry endpoint under a mount.
`RegistryAndHostDiscoveryUseTheirDistinctNormativeLocations` exercises both on
real Kestrel routes. No new discovery mechanism or permission to fetch advertised
destinations was invented.

## SPEC-004: Core XIDs retain relative-URI semantics

The clean NuGet consumer exposed a live HTTP federation failure for legal IDs
containing `:` and `@`. The server correctly escaped these in its `self` and
matching `xid`, but the federation parser treated escaped text as a decoded
Core ID. The independent specification helper had the same defect.

Core already defines XID as the Registry-relative URI path corresponding to
`self`. No protocol rule was changed: the example helper now decodes each
component once using strict UTF-8 and applies the unchanged ID grammar.
The original URI spelling is retained. Malformed escapes, separators, controls
and values requiring a second decode remain invalid.

The original Python helper rejected both uppercase/lowercase escaped examples;
.NET tests separately reproduced constructor and typed-identity mismatch
failures. `RegistryId.ParseEscaped`, HTTP source identity comparison and
Resource resolver comparison now share this distinction. The source's returned
XID/navigation are not rewritten to hide a mismatch. Previous tests incorrectly
classifying ordinary escapes as invalid now guard actual double-decode attacks,
alongside explicit positive URI-escape cases.

## SPEC-005: deterministic native routing and complete mapping URI identity

The interrupted native-binding work was recovered and completed. Directory
Mapping now compares decoded typed XIDs throughout selection, defaults,
references and collection materialization; File and managed Git use the same
interpreter. Stored XID spelling, raw Document bytes, opaque storage hrefs and
external content locators are retained.

OCI byte-range routing needed an actual draft correction, not more spelling
guesses. A key such as `/items/%61%3Aone` can occupy a different byte-ordered
shard from its equivalent selector `/items/a%3Aone`. The unreleased version-1
draft now specifies canonical URI components for graph identity annotations
and finite boundaries. The reader normalizes the selector once, follows one
interval per level, and rejects consumed noncanonical graph annotations.
Config metadata may retain equivalent URI spellings. Existing noncanonical
draft artifacts require explicit migration/regeneration with new digests.

Independent Python and .NET counterexamples demonstrated the ambiguity before
the fix. Native OCI tests verify strict rejection without mutating pinned
objects and verify that a canonical selector visits one leaf, not multiple
guessed aliases. The schemas and independent mapping/OCI validators follow the
same corrected contract; original baseline files remain unchanged.

## SPEC-006: pagination expiry uses the HTTP date format

The frozen pagination binding called its reference RFC3339 while actually
linking to RFC7234 section 5.3, which defines `Expires = HTTP-date`.
The correction names the HTTP format and fixes the example's mismatched
weekday (`Thu, 01 Dec 2021` becomes `Wed, 01 Dec 2021` without changing the
date/time). This is an editorial specification/example correction, not a new
wire convention or a relaxed parser.

`PaginationExpiryUsesHttpDateAndKeepsTheOriginalDeadline` independently asserts
the literal HTTP-date header through Kestrel, unchanged expiry on a subsequent
page and rejection at the original deadline. The two new Python timestamp
regressions failed against the old prose/example before the correction.
The .NET server already emitted HTTP-date; no production change was needed.
SPEC-002's original timestamp regression artifact is retained as the
predecessor of the SPEC-006 test-module successor.

## SPEC-007: Schema Registry Protobuf examples must be valid declarations

The Schema Registry example contained four Protobuf strings, each with an
unmatched closing brace after the `Metrics` message. All four failed the actual
Protobuf syntax validator on both .NET targets before the correction. Only the
extra brace is removed; fields, Version IDs, ancestors and default projection
are unchanged.

`EveryPublishedProtobufSchemaExampleIsValid` validates the captured successor
text, and `OriginalProtobufExamplesRemainRejectedForTheUnmatchedBrace` retains
the original counterexamples. The independent Python model-regression module
also pins the intended field declarations. Its SPEC-001 predecessor and the
original Schema specification remain immutable. No runtime validator change
or invented content-type inference was needed.

## SPEC-008: Message and Endpoint declaration contracts

Six independent Python source cases and twelve compiled .NET model executions
failed against field names, shapes and defaults that contradicted the domain
prose. Corrected Message sources declare `basemessage`, HTTP `status` and a
string-map `query`, and NATS `reply-to`. AMQP `subject.required` defaults to
false; Endpoint `messagegroups` targets Groups rather than Messages. The HTTP
query example and MQTT `payload_format_indicator` table spelling now agree
with their normative/model contracts.

The twelve Message/Endpoint/CloudEvents derived schemas were regenerated by
the existing generator. The original corpus and every earlier correction remain
immutable; seventeen successor inputs are recorded in SPEC-008. Existing
reviews were carried forward only after their exact IDs, text and source hashes
were revalidated (1,301 unchanged reviewed/informative rows, zero promotions).

`MessageEndpointModelCorrectionTests` now accepts the previously rejected
canonical declarations. Actual Server operations additionally reject malformed
HTTP status literals and method/status combinations atomically; Level-1 status
templates remain declarations for later substitution, not arbitrary expressions.
The old spellings are not silently aliased or injected into effective models.
URI targets still require the caller's explicit validation/access policy.

Typed-property values, general Endpoint template representation and Message
inheritance/type-selection workflows are separate remaining obligations, not
claimed fixed by these source corrections.

## SPEC-009: OpenUSD Groups must share the declared Resource type

OpenUSD section 4.4 declares plugin and asset-container `usdassets` to be the
same Resource type; section 5.3 permits Core one-hop aliases of that type.
The model instead declared two independent, similar types. One Python source
case and both-target .NET model/Server cases reproduced the mismatch, including
rejection of a plugin Group's alias to an asset-container manifest.

The plugin Group now imports `/usdassetgroups/usdassets` using the existing
Core `ximportresources` mechanism. Core type identity and alias checks remain
unchanged. Plugin-specific role/format/manifest-name rules still apply through
the containing Group's domain validation. No OpenUSD derived schemas exist in
this branch; the corrected model, independent source regression and feedback
are retained as three immutable successor inputs.

All 1,301 existing reviewed/informative rows retained their exact source/text
pins and were revalidated; this correction does not automatically promote
OpenUSD review claims or resolve the separate collision-only ID ambiguity.

## SPEC-010: property declarations must preserve domain literal values

The Message model used Core string attributes for typed property values and
Core object/map name rules for protocol metadata. Compiled Server operations
rejected boolean/numeric/structured declarations, lost literal null constraints
during completion and rejected valid AMQP symbol names. A Core `any` leaf alone
would not solve null deletion semantics.

CloudEvents envelope declarations and the five AMQP property-declaration
sections are now opaque Core values with explicit procedural Message checks.
Typed literals, fixed-property constraints and defaults are validated by shared
domain logic, not silently accepted as arbitrary content. The Core walker and
deletion rules remain unchanged. The corrected prose also removes the erroneous
CloudEvents `attributes` wrapper and defines an actual duration lexical profile
instead of citing a nonexistent RFC3339 duration grammar.

The source models and twelve dependent structural schemas are captured as
immutable successors. Two source tests and the public Server cases reproduced
the defects first. The current parent slice passes 61 cases per TFM, including
null preservation, domain defaults, required-property checks, exact integer
ranges, URI distinctions, Unicode restrictions and rollback. The corrected
independent source suite passes 940 cases with the same 38 OPC-UA exclusions.
Message materialization, Endpoint templates and the remaining source refinements
are separate implementation work, not inferred from structural schema success.

## SPEC-011: stable OpenUSD collision assignment and bounded lookup

The draft described a sibling collision suffix but did not determine whether an
existing binding had to be renamed when a colliding asset arrived. Stable
publication and subsequent lookup could disagree. Assignment now keeps the
noncolliding candidate unchanged and permits one suffix-reserved fallback
derived from the exact authoritative source. Published bindings are not renamed
or rebound; a secondary collision fails explicitly instead of inventing an
unbounded sequence.

The public consumer helper probes at most those two candidates and checks the
returned ID and exact authoritative `assetidentifier` before document acquisition.
Server regressions cover insertion order, deletion/restart stability and a real
32-bit prefix collision. Six independent source guards and both-target public
Models/Server cases pass. The corrected oracle at this stage passed 946 cases
with 38 explicit OPC-UA exclusions.

## SPEC-012: unresolved Endpoint options need an authoring representation

The Endpoint model rejected three permitted authored forms: an endpoint URI
containing `{tenant}`, a query Map key containing `{parameter}`, and a string
enum containing `{mode}`. All three compiled-model cases failed on both targets
before correction. A URI-leaf-only change cannot represent templated Map keys.

The six known `protocoloptions` sections now use an opaque authoring boundary,
with native JSON kinds and structural record names retained. This does not
change ordinary Core URI/name/enum validation or turn consumer-only protocol
requirements into passive-server checks. Consumer defaults are interpreted
without rewriting authored options. Endpoint/CloudEvents structural schemas are
regenerated; twelve successor inputs retain the old hashes and reproductions.

The three original compiled-model cases now pass on both targets. Two independent
source guards pass and the complete corrected source oracle passes 948 cases
with the same 38 OPC-UA exclusions. Actual Server authored-shape admission is a
separate integration gate, not proved by this source-only result.

## SPEC-013: common header references and MQTT binary/media types

HTTP, NATS, MQTT and Kafka header/property records omitted the common optional
`specurl`. MQTT `correlation_data` was incorrectly a URI template rather than
binary data, and `content_type` was modeled as a URI template while its table
called it a symbol. A normal media type with a parameter was rejected.

The four records now admit URI specification references. MQTT correlation data
uses a base64 string; media types use strings with the existing procedural MIME
checks and permitted placeholders. Runtime checks also reject malformed and
noncanonical Kafka `key_base64` without publication. Core URI, symbol and
deletion rules are unchanged.

The compiled Server cases reproduced missing reference admission, binary
validation and MIME-string rejection before correction. All 158 Message-focused
Server cases now pass per TFM, including the 21 new binary/header cases and
rollback assertions. Three independent source guards and the full corrected
oracle pass: 951 cases, with the same 38 explicit OPC-UA exclusions.
Sixteen immutable successor inputs include the twelve regenerated dependent
structural schemas. General Message inheritance and header type refinement are
not inferred complete from these representation corrections.

## SPEC-014: metadata-only field spellings are not Document content

Directory mapping's detachment rules treated `<RESOURCE>`,
`<RESOURCE>base64` and `<RESOURCE>url` as Document fields even when
`hasdocument: false`. Core permits those spellings as ordinary model-admitted
metadata in that case. Both the independent mapping writer/reader and .NET
reader rejected the permitted values.

Detachment and domain-URL restrictions now apply only to document-bearing
types. Metadata-only values remain exact JSON, including nested literal nulls;
their declared types are still checked, URL-looking strings are not fetched,
and Document operations still return `unsupported_operation`. Actual inline
Document fields and conflicting local-Document URLs remain rejected.

Nine public .NET cases pass on each TFM and the independent mapping suite passes
248 cases. Four immutable successor inputs preserve the original evidence.
All 39 mapping rows were re-reviewed: 37 unchanged clauses and two corrected
field-plane clauses, now 23 implemented and 16 pending broader evidence.
The complete corrected oracle passes 954 cases with 38 explicit OPC-UA exclusions.
An earlier all-green pytest run was correctly rejected by its provenance gate
because a concurrent parent review changed the protected ledger; its failed
receipt is retained, and the stable-ledger rerun is the qualifying source result.

## SPEC-015: OCI preserves metadata-only values and defaults

OCI had the same Document-field naming conflict as directory mapping. Its
reader and producer rejected explicit ordinary fields and could silently omit
required singular/base64-named defaults for metadata-only types. Twelve isolated
public probes reproduced those outcomes before the implementation fix.

OCI config validation now reserves Document names only when `hasdocument` is
true. Metadata-only values, captured nulls and effective defaults remain on the
metadata plane; mode, placeholder, closure, routing, digest and credential rules
are unchanged. The generic record schema needed no change. Eighteen new cases
cover reader/producer round trips, defaults, type failures and actual Document
restrictions. All 270 OCI cases pass on each managed and Windows x64 native TFM.

The source prose, independent oracle, new regressions and feedback are retained
as four immutable successors. All 60 current OCI clauses have source-pinned
reviews: 55 implemented, three informative, and two explicit compatible-copier
or external-copy qualification obligations not claimed by producer/resolver tests.

## SPEC-016: explicit oracle identities instead of unreported subtests

This is a **test-only successor**, not another normative protocol change.
Pytest 9 reported 960 XML tests for 957 emitted testcase records when three
successful `unittest.subTest` controls were included. The strict oracle gate
correctly rejected that otherwise passing run.

Each negative OCI field control now has its own test method and XML identity.
All original assertions remain, and neither the accounting validator nor its
required counts were relaxed. The corrected full oracle passes 959 explicit
cases with 38 OPC-UA exclusions, zero skipped cases and unchanged protected
sources. The rejected receipt and its exact mismatch remain available.

## SPEC-017: Catalog priorities are unsigned values, not token spellings

The Catalog source model uses Core `uinteger`, but procedural checks rejected
integer-valued decimal/exponent notation and negative zero. Six public cases
passed the compiled model then failed Catalog validation, reproducing the
inconsistency. Independent JSON Schema/decimal-oracle checks confirmed the
numeric-value interpretation.

Catalog validation and ordering now use the bounded exact-number implementation,
including stable ties and values beyond floating-point integer precision. Valid
tokens are preserved; true fractions, negative nonzero values, nonfinite values
and wrong JSON kinds remain invalid. The source prose makes that constraint
explicit and the independent procedural checks agree. The raw model and derived
schemas needed no changes.

Five immutable successor inputs retain the clarification and its regressions.
The focused Catalog suites pass 110 Models, 86 Federation and 108 Server cases
per TFM; 289 independent in-scope federation/priority cases pass. The current
200-row Catalog/OpenUSD review incorporates this correction instead of leaving
the identified interpretation blocker unresolved.

## SPEC-018: coupled Registry priority-vector correction

The full source oracle found one old Registry vector still expecting `0.0`
priority rejection. Its selected-object, generated-schema and nonmutation
assertions now expect the value semantics clarified by SPEC-017. This test-only
successor preserves the old receipt; the complete corrected oracle then passed
971 cases with the same 38 OPC-UA exclusions.

## SPEC-019/020: complete common header declarations

HTTP/NATS/MQTT/Kafka declarations lacked the common `type` field, and MQTT lacked
`required`. Opaque item boundaries preserve raw member presence for procedural
validation, while outer array/map rules and duplicate array order are unchanged.
Native string-valued refinements do not invent wire coercion; Kafka's binary
base64 and nullable byte values have explicit representations. Shared completion
supplies string/false defaults only for declarations actually present.

SPEC-019 captures the model/prose changes and regenerated dependent schemas.
The first compiled-model run caught documentation attached to an item shape
where Core does not permit annotations. SPEC-020 moves that documentation to
the owning attributes rather than relaxing Core. Earlier inputs remain
immutable. Public Server/materializer tests cover common fields, invalid nulls,
native-value restrictions, exact output bounds and rollback; independent source
guards cover all four item boundaries.

The final Message review also found a runtime-only defect: local `basemessage`
admission demanded a generic host target validator even for a missing Message.
The Server now checks the local model/type/path without requiring existence or
acquisition, filters only that specific discharged obligation, and retains
unrelated custom-model policies. No specification change was required for this
fix. The consumer still exposes unavailable bases as explicitly incomplete
results and enforces cycle, ownership and traversal limits.
