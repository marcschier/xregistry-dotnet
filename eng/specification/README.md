# Specification provenance and requirement inventory

Run from the repository root:

```powershell
python eng\specification\manage.py verify
python eng\specification\manage.py check
python -m unittest discover -s tests\Tooling -v
pwsh -NoProfile -File eng\test-specification.ps1
```

`verify` checks every pinned file's path, byte length and SHA-256 against
`eng/specification-lock.json`, including the retained source license. It rejects
unlisted files and does not need a sibling specification checkout or network.
`check` also rejects stale generated requirement/report outputs.

`test-specification.ps1` runs **two separate independent pytest invocations**:
the frozen original corpus and the active corrected corpus. Dependencies are
provisioned from `eng/requirements-oracles.lock` using `--require-hashes`; the
runner never installs packages or uses a live sibling specification checkout.
Both runs select the seven explicit HTTP/File/Git/OCI/catalog/federation standard
modules. The corrected run also selects active
`tools/test_implementation_[a-z0-9_]+.py` regression files. Manifest `testIds` are
provenance metadata, never executable selectors.

After `verify_corpus` succeeds, `run_corrected_oracles.py` materializes a new
`artifacts/specification-oracles/<mode>-<id>/overlay` from all locked original
paths, including `LICENSE`, and overlays only `correction_records`' active heads
in corrected mode. Added regression files are included. Every copy is a separate
file; source permissions and links are not propagated. All temporary test/Git
stores stay in that run's artifact directory. Original files, all historical
corrections, the lock/scope, manifest and existing requirement ledger are hashed
before and verified again after execution, including failure paths. Overlay
mutation, missing/unlisted files and links also fail the run.

Mixed modules contain OPC-UA-named parameter cases, so the fixed selector is
`-k "not opcua"`; no OPC-UA helper is imported by the module inventory. External
pytest configuration, conftests, plugin autoload and inherited Python/pytest
options are disabled. Every selected module must execute nonempty tests. The
runner reconciles actual collected node IDs and setup/call/teardown outcomes
against pytest XML case identities and totals. Empty runs, skips/xfails,
failures, errors, missing reports, different frameworks, unexpected deselections
and nonzero exits cannot produce a passing receipt.

Each run retains its own `report.json`, `results.xml`, `accounting.json`,
`control.json`, `pytest.log` and `LICENSE`. Reports include the correction-manifest
hash, every overlay file's origin/hash/size, protected before/after hashes, exact
module list and selector, actual runtime versions, and separate XML/collection
counts. The CI `specification-oracles` job invokes the PowerShell wrapper and
uploads these reports even on failure; it never adds the original and corrected
pass counts together.

```powershell
python -B eng\specification\run_corrected_oracles.py --mode corrected
python -B eng\specification\run_corrected_oracles.py --mode original
```

`--output` accepts only a new run directory beneath
`artifacts/specification-oracles`; existing outputs are never reused.
`--timeout-seconds` defaults to 600 and is capped at 900 per invocation (also
available as `test-specification.ps1 -TimeoutSeconds`). Logs are capped at 8 MiB,
XML at 16 MiB, and cancellation terminates the owned Windows Job/POSIX process
group, with bounded cleanup waits. These checks validate the supplied Python
oracle corpus, **not .NET conformance or native qualification**.

The baseline run executed 898 in-scope cases successfully and explicitly
deselected 38 OPC-UA-named cases. Nine existing OpenAPI-validator deprecation
warnings were retained; no .NET conformance credit is derived from those
Python results.

The initial `import --snapshot <directory>` accepts only the exact frozen
manifest hash in `scope.json`. It validates all inputs before copying, excludes
presentation artwork, and refuses to overwrite changed source evidence or a
different lock. A specification correction therefore needs an explicit reviewed
successor lock/scope, not a silent import of today's upstream branch.

Evidence-backed changes can instead be captured with
`capture_corrections.py --source <approved-spec-checkout> --case SPEC-NNN
--reason <explanation> --test <regression-test> <explicit-paths...>`.
Corrected bytes live separately under `tests/Conformance/Corrections`; neither
the original corpus nor previously captured corrections are overwritten.
`correction_records(root, original)` still returns one **active** record per path,
so active model resources and clause extraction use the latest verified successor.
`verify` checks the original inventory and every historical corrected file, not
just active heads, and rejects unlisted files, path aliases, links and collisions.

Correction manifest version 2 has exactly `schemaVersion`,
`baselineManifestSha256` and an ordered `entries` list. Each entry retains the
version 1 fields `case`, `path`, `originalSha256`, `sha256`, `bytes`, `file`,
`sourceCommitBeforeCorrections`, `reason` and `testIds`, and requires two more:

| Entry for a path | `predecessorSha256` | `predecessorCase` |
| --- | --- | --- |
| First correction of a frozen source | Frozen source hash | `null` |
| First correction of an added source | `null` | `null` |
| Subsequent correction | Immediately preceding correction's hash | Its case |

`originalSha256` always remains the frozen hash, or `null` for an added source.
Each path's cases must strictly increase as `SPEC-NNN`, without reuse, branching
or reordered predecessors. Different paths may share a case and interleave in
the list. Inventory paths use canonical forward slashes; `file` must be exactly
`tests/Conformance/Corrections/<case>/<path>`. Reasons and regression test IDs
must be nonempty, test IDs unique, and each file and the manifest at most 32 MiB.

Version 1 manifests remain readable without changes. The first capture that
adds records upgrades the manifest to version 2, retaining **all** existing
entries in their original order and adding the first-entry predecessor fields
above. Recapturing the active case with identical bytes does not rewrite or
migrate anything, including the recorded commit, reason and test IDs. Different
bytes under an already captured case fail explicitly; use a higher case for
that path. Earlier cases cannot be reused once a successor is active.

Capture validates the entire proposed history and all inputs before writing.
The shared `validate_corrections(root, original, document, pending=...)` preflight
accepts a map of new canonical evidence-file paths to bytes and returns the same
active-record map without writing. Capture creates new files exclusively, then
atomically publishes the manifest. Detected publication failures remove only
newly created artifacts; cleanup failures are reported explicitly. This is not
a multi-file crash-recovery protocol: interruption can leave unlisted artifacts,
which verification rejects rather than silently adopting or deleting.

## Review workflow

`generate` extracts uppercase normative keywords from prose blocks and table
rows in the seventeen explicitly listed normative documents. It records
content-derived IDs, exact line ranges, section paths, source hashes and family
owners. Fenced examples and HTML comments are not keyword requirements.

Extraction is deliberately **not semantic completeness**. One block can contain
multiple obligations; algorithms, schemas and examples can impose requirements
that keyword extraction misses. Review each family and its artifact inventory,
split or supplement procedural cases through a reviewed tool/schema evolution,
and map actual public test cases. Do not fabricate case IDs.

Edit each requirement's `review` and `implementation` fields in
`tests/Conformance/requirements.json`. Reviewed rows need a rationale and
applicable roles. Implemented rows need reviewed test IDs. `generate` preserves
these fields, rejects removed IDs and changed reviewed-source hashes, and
regenerates `docs/conformance.md`.

The root `semanticCoverageReviewed` value stays false until the complete
procedural/artifact inventory has actually been reviewed. Changing it is a human
review assertion, not an automatic result of keyword extraction or test counts.
Any correction-manifest byte change, including history-only changes or migration,
changes `correctionsSha256`. Completed semantic reviews and reviewed/informative
rows in an otherwise pending ledger require explicit reset and repeat review.
An entirely unreviewed ledger must be regenerated before `check` succeeds;
regeneration does not turn pending work into qualification evidence.

## Native execution receipts and release

`release` first performs consistency checks, then rejects incomplete semantic
review, empty inventories, unreviewed/unqualified requirements, missing native
cells and changed or invalid execution receipts. Each applicable requirement
needs the eight net8/net10 Windows/Linux x64/ARM64 cells.

A receipt referenced by `nativeEvidence` contains `schemaVersion: 1`,
`framework`, `rid`, `nativeExecutable: true`, `passedTestIds`, `failedTestIds`
and `skippedTestIds`. The report's SHA-256 must match; failures/skips must be
empty and all mapped tests must be in the executed pass list. CI must produce
receipts from actual runner/native-process evidence, not synthesize green data.
Receipt structure and hashes are consistency checks, not signatures or proof
against a malicious release operator.

The current release command is expected to fail while the implementation is
incomplete. A green provenance check must never be represented as conformance.
