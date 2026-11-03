# Specification corrections and verification

The original specification baseline remains byte-identical under
`tests/Conformance/Sources`. Reviewed successor artifacts are stored separately
under `tests/Conformance/Corrections`; the ordered manifest
`eng/specification/corrections.json` records each canonical source path,
original hash, predecessor hash/case, successor hash and size, reason, and
regression test IDs.

The active corrected corpus is a verified overlay. It never rewrites the
original baseline or an earlier correction. The historical SPEC-001 through
SPEC-020 before/after ledger is in the
[changelog](changelog.md#specification-correction-ledger).

## Verify the corpus

Run from the repository root:

```powershell
python eng\specification\manage.py verify
python eng\specification\manage.py check
python eng\sync_models.py --check
pwsh -NoProfile -File eng\test-specification.ps1
```

`verify` checks every original and corrected file's path, byte length, SHA-256,
manifest chain, and retained license. It rejects missing, unlisted, linked,
aliased, colliding, reordered, or changed evidence. `check` additionally rejects
stale generated requirements and `docs/conformance.md`.

`eng\test-specification.ps1` installs the independent Python oracle dependencies
from `eng/requirements-oracles.lock` with `--require-hashes`, then runs the
original and corrected corpora as separate pytest invocations. The runner
disables ambient plugins/options, uses the fixed in-scope module inventory and
`-k "not opcua"` selector, requires nonempty explicit case identities, and
reconciles collection and XML accounting. Original and corrected pass counts
are never added together or converted into .NET conformance credit.

## Correction capture

Capture an approved correction with:

```powershell
python eng\specification\capture_corrections.py `
  --source <approved-spec-checkout> `
  --case SPEC-NNN `
  --reason <explanation> `
  --test <regression-test> `
  <explicit-paths...>
```

Capture validates the complete proposed history before writing. New evidence
files are created exclusively, and the manifest is atomically replaced only
after all paths, hashes, predecessor links, reasons, and test IDs validate.
Recapturing identical bytes for the active case is a no-op; different bytes
require a later SPEC case. Verification rejects interrupted unlisted artifacts
instead of adopting or deleting them.

The correction workflow does not import a moving upstream checkout, fetch
network content, alter protected evidence, or change requirement qualification.
See [the specification tooling guide](../eng/specification/README.md) for the
manifest schema, overlay limits, requirement-review workflow, and native receipt
contract.

## Packaged model provenance

`python eng\sync_models.py --check` verifies that
`src/XRegistry.Models/Resources` matches the pinned active corpus and included
source license. Those packaged resources are generated evidence and must not be
edited directly. Model loading and compilation behavior is documented in
[built-in model sources](models.md).

The current correction set covers source/model/example/tooling defects through
SPEC-020. A corrected source or passing independent oracle is evidence about
that corpus only; it does not qualify package behavior, native platforms,
interoperability, or release status. Current extracted requirement counts and
qualification status are in [conformance evidence](conformance.md).

## Selected compatibility-admission proposal

The implementation selects the W27 compatibility-admission correction from
[xregistry/spec#677](https://github.com/xregistry/spec/pull/677) at
`b0d2d81777dace4361227a9c37ddf5cd6629a563`. The exact Core source and license
are retained under `tests/Conformance/Successors/pr677-b0d2d817`.
This is proposed behavior, not a merged Core requirement.

The selection covers the `compatibilities` capability's configuration-error
scope and the Meta `compatibility` admission/change rules. It does not select
the other changes in that PR or replace the active merged Core source.
The source file remains review-only in the extraction inventory so unrelated
or older clauses cannot supersede merged requirements through this selection.

A stored compatibility declaration must satisfy the Resource model, including
its enum restrictions. Checker capabilities describe validation support rather
than storage admission. When compatibility checking is disabled, an unavailable
checker does not reject a permitted declaration and validation flags remain
absent. When enabled checking is unsupported, non-strict validation records
`compatibilityvalidated=false` with a reason; strict validation rejects the
operation. An actual compatibility violation rejects the entire operation in
either mode, without changing stored metadata, Versions or events. Changing
the declaration evaluates all applicable Versions, not only new Versions.

`DisabledCompatibilityAdmitsModelModesWithoutAnEnabledChecker`,
`UnsupportedCompatibilityModeIsUnknownRatherThanAnEstablishedViolation` and
`CompatibilityViolationRollsBackEveryVersionRegardlessOfStrictValidation`
exercise these outcomes through the public engine API. This proposal selection
does not convert the conflicting merged admission clause into implemented
standard conformance or supply native qualification.

## Selected cross-reference creation-event proposal

The implementation selects the narrow creation-event exception in
[xregistry/spec#686](https://github.com/xregistry/spec/pull/686) at
`9e103e2be4de6a7d90630bd2b346e4d08c029cf1`. Its exact Events source and license
are retained under `tests/Conformance/Successors/pr686-9e103e2b` as review-only
inputs. Only the Resource-created Version-pairing exception is selected, not
the whole older Events source or the PR's withdrawn proposals.

Creating an xref Resource generates Resource-created without synthesizing
Version-created events for its target's existing Versions. Versions actually
created elsewhere in the same interaction retain their own events.
`CrossReferenceResourceEventsUseTheFinalVisibleEpochs` checks the complete
event-type set for direct and second-hop references.
`CrossReferenceAndLocalResourceCreationOnlyReportsTheNewLocalVersion` checks
that a sibling local Resource's Version-created event remains present in the
same interaction. Ordinary Resource creation retains its required pairing.

This selection changes neither the existing event-data epoch requirements nor
the unresolved policy for monitoring target changes or obtaining data from an
unavailable target. It is an open proposal, not merged Events conformance.

## Active Message artifact differences

The merged `upstream-d290b1ec` Message model still differs from the packaged
model. The packaged bytes match the approved SPEC-020 correction, not the
uncorrected merged artifact; `sync_models.py --check` verifies that distinction.
These differences therefore need explicit disposition rather than a claim that
the two models are identical.

| Artifact difference | Current disposition |
|---|---|
| HTTP prose requires a query string map; the merged model and example use an array. The model also omits the prose's `status` property. | The approved correction supplies the map and status in the packaged model. Domain validation rejects query arrays. Proposed source correction: use a query string map in the model and example, and declare `status`. |
| MQTT/5.0 prose describes binary `correlation_data`; the merged model calls it `uritemplate`. Its `content_type` also uses `uritemplate` rather than a media-type string. | SPEC-013 supplies canonical base64 and media-type string declarations, retained in SPEC-020. Proposed source correction: use the binary representation for correlation data and a string for content type. |
| CloudEvents and AMQP declaration maps use ordinary Core object/map traversal in the merged model. | Approved corrections preserve literal nulls and protocol names through opaque boundaries, with mandatory domain validation. This is an implementation authoring boundary, not permission to ignore invalid declarations. |
| HTTP/NATS headers, MQTT user properties and Kafka header items lack the complete common declaration fields in the merged model. | SPEC-020 retains opaque declaration items with domain checks and defaults. Their documentation belongs to the owning attribute because Core item shapes do not admit annotation fields. |
| The HTTP heading lists HTTP/1.1, HTTP/2 and HTTP/3, while the body, model and example select `HTTP`. | The implementation uses the declared `HTTP` selector; it does not invent equivalence for versioned custom selectors. Proposed source clarification: distinguish wire versions from the Message protocol selector. |
| Kafka prose calls topic/key strings and key_base64 binary; the model calls topic/key URI templates and key_base64 a string. | The domain preserves string values and Level-1 templates, and validates canonical base64 independently of Core shape. Whether the model's named types should be changed remains a source-artifact decision, not evidence of full Kafka wire qualification. |

These observations do not change the frozen source composition or edit upstream
text. Complete runnable example/schema comparison and imported protocol
qualification remain open. Registry definition admission does not serialize an
application message or prove that a broker accepts it.
