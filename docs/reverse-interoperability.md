# Pinned upstream CLI to native .NET interoperability

**Current result: the real Windows mutation/read/restart slice passed 17 named
checks; full reverse qualification remains blocked by the pinned upstream
checker.** `xr conform` exited 2 with **30 pass, 10 fail, 0 warn, 0 skip**.
The harness exits nonzero and records `qualified: false`. The new CI lane
retains this failure rather than filtering capabilities, patching the peer,
lowering totals or skipping suites.

This direction is independent of the existing .NET-client-to-upstream and
managed-Git jobs. Those jobs were preserved unchanged.

## Exact independent oracle

`interop\upstream-lock.json` remains the authoritative upstream source/image
pin. The reverse lane builds the CLI from public source instead of requiring
Docker:

| Item | Pin/evidence |
| --- | --- |
| Repository | `https://github.com/xregistry/server` |
| Commit | `5854af0130db7723bad489f16ba66536126b823a` |
| Tree | `e6ba7cff56b20cf1871ca2ed04075ae93c843571` |
| Actual `xr --version` | `Version: 5854af0130db` |
| Windows CLI SHA-256 | `0fd41422c957733018adf701a89d6569ab77643a023df0994843b3a8bbe2f660` |
| Go toolchain | `go1.27.1`, exact official archive pins in `interop\reverse-toolchain.json` |
| `go.mod` SHA-256 | `1625dbcdc8fde6e227aee5cf16a9b03858797b8135765af91d67ff5e6f94585a` |
| `go.sum` SHA-256 | `eb7f3550531ef7350759097d685dd384264053a7b2f8b5ec2908197ff7808d8b` |

The upstream README, installation/developer documentation, Makefile and module
locks were inspected at that exact commit. The builder follows the Makefile's
`.sharedfiles` recipe: substitute `XXX` with `registry` or `xrlib` in the two
shared templates to create the four generated Go files. It then builds the
unmodified `cmds/xr` package with the official
`common.GitCommit=<full commit>` linker value, `-mod=readonly`, `-trimpath` and
VCS metadata enabled. `go mod verify`, module-file hashes, tracked-source
cleanliness, embedded VCS revision and `vcs.modified=false` are checked.

Go was absent on the local Windows host. After that actual missing-tool
failure, the exact official Go archive was SHA-256/size checked and installed
only inside a unique artifact directory. A second complete bootstrap through
the new helper also passed. Global Git configuration/hooks and interactive
credentials are disabled for the task checkout. GOROOT, GOPATH, module/build
caches and CLI configuration are isolated; no user repository, global tool
installation or .NET runtime dependency is involved.

The image remains pinned in the existing lock as
`ghcr.io/xregistry/xrserver-all@sha256:f22068a09279de14b95cd7e05bb989ab6d7cc5b1c543c30f1fb2ca90fd20a186`;
it was not used by this reverse run. Docker Desktop was not restarted or killed.

## Native server and fixture

The local run used the existing
`artifacts\file-server-native\XRegistry.Sample.FileServer.exe`:

- SHA-256:
  `88e763cd55c61685e96f2c1d5ade585fcc80575bc130b4c7f9b5ed099e9b7bd1`.
- Actual runtime proof:
  `{"nativeAot":true,"jitCompiledMethods":0,"architecture":"X64"}`.
- Both the RuntimeFeature-derived flag and zero JIT-compiled-method count are
  required; a single flag alone cannot pass.

The harness reuses `eng\verify_file_server.py` for owned-process startup,
readiness, explicit loopback-demo arguments and exact process cleanup. Only the
owned server is terminated. It uses local NTFS on Windows; no network storage
or Docker service is configured. A separate restart reopens the same store.

`interop\reverse-fixture.json` independently declares one `dirs` Group type,
one `files` document-bearing Resource type, one Group/Resource instance, and
Versions `v1`/`v2`. The model is installed through ordinary
`PUT /registry/modelsource`. The actual unmodified CLI then performs:

```text
xr --config <isolated-config> --server <owned-root> import /dirs --data @<fixture>
xr --config <isolated-config> --server <owned-root> get /dirs
xr --config <isolated-config> --server <owned-root> get /dirs/team/files/item
xr --config <isolated-config> --server <owned-root> get /dirs/team/files/item/versions/v1
xr --config <isolated-config> --server <owned-root> get /dirs/team/files/item/versions/v2
xr --config <isolated-config> --server <owned-root> get /dirs/team/files/item/meta
xr --config <isolated-config> --server <owned-root> get /dirs/team/files/item/versions/v1 --details
xr --config <isolated-config> --server <owned-root> get /dirs/team/files/item/versions/v2 --details
```

The pinned CLI's `get.go` explicitly writes document bytes without adding a
newline. The harness compares raw stdout to independently chosen bytes
`00 FF 7F 0D 0A 41`, including the NUL/high-bit byte and CRLF. `v2` is an actual
zero-byte document; `v1` remains explicitly sticky default. Metadata identifies
the expected Resource/Versions and configured PublicRoot. These bytes, default
and metadata survive owned-process termination/restart.

Both discovery locations are checked:
`/.well-known/xregistry` and `/registry/.xregistry`. Requests deliberately send
`Host: spoofed.invalid`; advertised root links must still use PublicRoot.

No proprietary prepare/replay protocol or authentication bypass is introduced.
The sample's explicit loopback demo grants local administration as designed.
Production authentication/TLS are outside this fixture.

## Conformance blocker: pinned capability parser

The native server returns:

```http
GET /registry/capabilities
Host: spoofed.invalid

HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
```

Its capabilities include `"mutable":[]`, alongside `available`, flags,
formats, compatibility modes, pagination, specversions and versionmodes.
The frozen `core\spec.md:1848-1886` explicitly includes `mutable`, permits
extension capability names, and requires supported capabilities to be
serialized even for empty lists.

The pinned peer's `common\capabilities.go:ParseCapabilities` uses strict
unmarshalling and rejects that field. The actual checker output includes:

```text
FAIL: TestCapabilities
FAIL: Parsing capabilities MUST work
Unexpected error: Unknown capability specified: mutable.
FAIL: Parsing Capabilities MUST work
Unexpected error: Unknown capability specified: mutable.
Pass: 30   Fail: 10   Warn: 0   Skip: 0
```

`TestRegistryRoot`, `TestGroups` and `TestResources` then fail their cached
dependencies rather than completing their checks. This is recorded as
`XR-CAPABILITIES-MUTABLE` in `interop\reverse-known-limitations.json`, not
reported as a .NET server bug or a successful conformance run.

The strict expectation remains the six named suites and **101 clean passes**
for the single-group/single-resource, inline-enabled fixture. That branch shape
matches the qualified pinned-Go fixture in `upstream-lock.json`. A clean
reverse result has **not** been established. No 30-pass reduced success,
missing suites, warning/skip allowance, response filter, modified CLI or
specification change is used to manufacture it.

A later read-only check of the upstream default branch (`master`) resolved to
`4e29b6b32118059ada63a7b321e3984ec8783459`. Its `common/capabilities.go` still
omits the top-level `mutable` field, and `common/utils.go` still calls
`DisallowUnknownFields` in `Unmarshal`. Updating to that unmodified revision
therefore does not remove this incompatibility. This is immutable source
evidence, not a newly built/qualified peer; the existing interoperability pin
was not changed.

## Tools, evidence and commands

Build the oracle into a fresh task directory:

```powershell
$work = Join-Path 'artifacts\reverse-interop' ('run-' + [Guid]::NewGuid().ToString('N'))
python eng\verify-upstream-to-dotnet-build.py --work $work --bootstrap-go

python eng\verify-upstream-to-dotnet.py `
  --server artifacts\file-server-native\XRegistry.Sample.FileServer.exe `
  --oracle-build (Join-Path $work 'oracle-build.json') `
  --data-root (Join-Path $work 'data-parent') `
  --output (Join-Path $work 'qualification')
```

An explicitly supplied already-installed pinned Go executable can replace
`--bootstrap-go` using `--go <path>`. `--source` accepts only an exact clean
checkout beneath the selected task directory. The helper never runs `make all`,
starts MySQL, publishes images or invokes source-controlled hooks.

The final actual Windows artifacts are:

```text
artifacts\reverse-interop\bootstrap-6e79dc23d2614d33a3af067985ef5798\
  oracle-build.json
  go-build-metadata.stdout
  qualification\evidence.json
  qualification\xr-*.stdout
  qualification\xr-*.stderr
  qualification\capabilities.body
  qualification\server-initialize.log
  qualification\server-restart.log
```

`interop\reverse-windows-evidence.json` contains the compact actual-run
descriptor and hashes. Reports retain command arguments, cwd, exit codes,
raw CLI stdout/stderr, exact HTTP response bodies/headers, native proof,
model/fixture identity and the checker failure. No secrets are configured.
Unexpected HTTP statuses retain their wire evidence before the harness fails.

The final execution reports 17 passed named cases, one failed `xr-conform`
case and no unexecuted planned cases. Overall `status` is `failed` and
`qualified` is `false`.

## Fail-closed tooling tests

```powershell
python -m unittest discover -s tests\Tooling -p 'test_reverse_interop*.py' -v
python -m unittest discover -s tests\Tooling -p 'test_check_upstream_output.py' -v
```

The final runs passed **18 reverse tooling tests** and **6 existing checker
accounting tests**. These synthetic negative inputs are harness tests, not a
fake CLI substituted for interoperability.

| Failure class | Test |
| --- | --- |
| Wrong/missing native flag, nonzero or boolean JIT count, wrong architecture/exit | `test_native_proof_requires_flag_zero_jit_count_architecture_and_clean_exit` |
| Malformed, duplicate-key, non-finite, truncated/trailing or oversized JSON | `test_malformed_duplicate_nonfinite_trailing_and_oversized_json_cannot_pass` |
| Wrong CLI identity or success-shaped output with failure exit | `test_pinned_cli_version_is_exact_and_failure_exit_does_not_count_as_version_proof` |
| Empty fixture/model or absent real Groups/Resources/Versions | `test_empty_or_changed_fixtures_are_rejected_before_a_server_can_start`, `test_seed_observation_requires_the_actual_document_model_group_resource_and_versions` |
| Altered binary stdout or empty-output error mistaken for an empty document | `test_binary_stdout_is_exact_and_an_empty_document_is_not_an_error_exit` |
| Native process exits before readiness or endpoint is unreachable | `test_native_server_exit_before_readiness_is_an_explicit_failure`, `test_unreachable_loopback_endpoint_fails_instead_of_becoming_empty_metadata` |
| Command timeout/output flood | `test_owned_command_timeout_is_reported_and_process_is_reaped`, `test_owned_command_failure_and_output_limits_cannot_be_success` |
| Checker exit/totals/suites/warnings/skips or known blocker | `test_checker_requires_six_named_suites_exact_totals_zero_errors_and_zero_process_exit`, `test_known_mutable_capability_failure_is_classified_but_not_accepted` |
| Source/toolchain/build-metadata/archive pin or traversal mismatch | `test_reverse_lock_must_agree_with_exact_existing_upstream_pin`, `test_binary_build_metadata_requires_unmodified_exact_vcs_commit`, `test_archive_hash_and_size_are_verified_before_extraction`, `test_archive_cannot_write_outside_its_owned_go_directory` |

## CI lane and remaining claims

`.github\workflows\interop.yml` adds only
`pinned-xr-to-native-fileserver`. Existing forward/Git workflow bytes were
preserved. The job reuses the existing pinned checkout, .NET, Python and
artifact actions, uses contents-read permissions, builds the immutable CLI and
native Linux x64 FileServer, and creates a dedicated verified ext4 loop
filesystem for the writer. Only that owned mount/backing file is cleaned up.
Docker is not required. Evidence is retained even on failure.

**The lane currently fails its strict conformance step on the known peer
parser issue.** There is no `continue-on-error`, skip flag, reduced expected
count or accepted-error path. YAML/job structure was validated locally; the
Linux job itself was not executed or remotely dispatched during this task.

No claim is made for full Core coverage, a clean six-suite reverse checker run,
Linux execution, ARM64, native bridge behavior, live production TLS/auth,
publisher signatures or general interoperability with arbitrary clients.
No source/spec/server correction was made to appease this peer.
