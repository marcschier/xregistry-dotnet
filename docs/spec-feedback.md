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
