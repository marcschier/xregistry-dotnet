# Releasing xRegistry .NET

**Build artifacts are available without publishing authority.** The source
version is `1.0.0-rc4`. Packages and samples are implemented but not
release-qualified; remaining native/conformance requirements must not be marked
qualified merely to unblock promotion.

`packages.yml` runs locked builds and `eng\package_build.py`, producing the exact
source version as eleven `.nupkg`/`.snupkg` pairs under `artifacts\ci-packages`.
`package-build.json` records their hashes, source commit, ref and explicitly
`releaseQualified: false`. The build job has no publishing token or OIDC
permission. PR artifacts cannot substitute for a qualified tagged release.
No NuGet packages or release tags are created by an ordinary `main` push.

Parallel MSBuild workers do not export NBGV version variables to the shared
GitHub Actions environment file. `NBGV_SetCloudBuildVersionVars=false` prevents
interleaved writes without disabling version calculation, assembly metadata or
NuGet source provenance. Workflows consume the explicit artifact manifests
instead of those implicit environment variables.

## Profile and version contract

The release contract uses `tag_workflow: release.yml`, `promote: nuget-yml`
and environment `release`. The corresponding files are
`.github\workflows\release.yml` and `.github\workflows\nuget.yml`.
Any separate profile automation must use the RC version explicitly instead
of assuming the old `alpha` channel; that repository is administered separately.

`nuget.yml` accepts **one required `workflow_dispatch` string input named
`version`**, with no default. Dispatch must select `main`. The conductor supplies
an explicit NuGet version such as `1.0.0-rc4`, not `v1.0.0-rc4`.
The exact source ref is then `refs/tags/v1.0.0-rc4`.

Versions must be lowercase canonical SemVer, at most 64 ASCII characters, with
three numeric components in the NuGet signed-32-bit range. Leading numeric
zeroes, whitespace, a `v` prefix, build metadata, uppercase prerelease aliases,
path separators and shell metacharacters are rejected. Stable versions are
supported; no prerelease channel is inferred. Input enters the helper through an environment variable, never through
interpolated shell source.

The package allowlist comes from `eng\packages.json`, not filename globs.
Exactly eleven package IDs, `net8.0;net10.0`, all four
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64` RIDs, and exactly the three
declared .NET 10 samples are required. The publishing checkout's trusted
inventory must agree with the tagged inventory's IDs. Each ID contributes one
`.nupkg` and one `.snupkg`, matching the repository's existing symbol-package
settings; samples are not NuGet artifacts.

## Tag build and provenance

`release.yml` handles only new `v*` tag pushes. It rejects malformed versions,
deleted/forced/updated tags, the wrong repository/workflow source, a dirty
checkout, or a tag whose peeled commit differs from the checkout and GitHub
event identity. Both lightweight and annotated tags are supported. The annotated
tag object is recorded as well as its peeled commit.

The build job has only `contents: read`, no publishing token and no OIDC access.
`eng\release\release.py build` performs these steps in order:

1. Require all eleven libraries and three samples to be `qualified`, then run
   the existing `python eng\check_packages.py --release` and
   `python eng\specification\manage.py release` gates without changing them.
2. Run `eng\build.ps1` (locked restore, warnings-as-errors build and project
   evaluation), then `eng\test.ps1 -NoBuild` (tooling, source consistency and
   nonempty managed tests). A failure stops artifact creation.
3. Pack each declared library with explicit `PackageVersion`, `Version`,
   `RepositoryCommit`, `RepositoryBranch`, `PublicRelease` and CI properties.
   `--no-restore` preserves the locked dependency graph. `eng\pack.ps1` remains
   unchanged; the small release-specific pack loop is necessary because its
   existing interface has no explicit-version parameter.
4. Recheck the clean checkout and the tagged bytes of the package inventory,
   specification lock, correction manifest and requirement ledger. Validate the
   complete package/symbol inventory, both framework outputs, and each nuspec's
   ID, exact version, repository URL, commit and tag ref.
5. Assemble the flat payload and authoritative `release-manifest.json`, including
   the repository ID, tag object, commit, workflow, run ID/attempt, qualification
   commands, byte lengths and SHA-256 of every payload file. The source
   inventories and unchanged native receipt bytes are included.

Every applicable requirement must remain reviewed and qualified, with exactly
eight distinct framework/RID cells. The existing specification validator must
accept each receipt's hash, native-executable flag and mapped executed test IDs,
without failures or skips. Missing evidence is an error. The helper only copies
and verifies existing receipts; it does not generate native pass lists.

The payload is uploaded once under
`xregistry-release-<version>-<commit>-<run-id>-<attempt>`, with overwrite disabled,
missing files treated as errors, zero ZIP recompression, and 90-day retention
(subject to repository policy). A separate job, with no checkout or execution of
package code, uses OIDC and `attestations: write` to attest the **uploaded ZIP's
SHA-256**. The run must succeed including that signing job. There is no GitHub
Release creation or automatic NuGet promotion.

The attestation establishes the artifact's build/workflow provenance. It is not
an independent certification of protocol behavior or a replacement for human
semantic review. Existing native receipts are the ledger's consistency evidence,
not independently authenticated execution attestations. This slice adds no
native producer or conformance credit and does not claim that today's incomplete
native workflow covers the entire library/sample matrix.

## Approval-separated promotion

The read-only `resolve` job runs trusted code from the exact `main` workflow
commit selected at dispatch. It does not check out or execute downloaded/tagged
release code.

It resolves the exact tag via GitHub's Git API, including bounded annotated-tag
peeling, then requires **exactly one** completed, successful `push` run of
`release.yml` for that tag, commit and repository. Pagination is complete and
bounded; missing, truncated, ambiguous or inaccessible results fail. There is
no latest-run, latest-version, latest-success or alternate-artifact fallback.

Exactly one unexpired artifact with the expected run/attempt-specific name and
GitHub SHA-256 is required. The artifact's source repository IDs, run ID, tag and
commit must agree. The helper downloads that artifact **by ID**, verifies its ZIP
size/hash and signed SLSA provenance using `gh attestation verify`, requiring the
exact repository, signer workflow, signer/source commit and tag ref, and rejecting
self-hosted signers. Each qualification source file must match bytes fetched
from the exact tagged commit, not from `main` or a moving branch.

ZIP extraction never uses `extractall`. The format allows only flat regular
files. Traversal, absolute/UNC/drive paths, backslashes, alternate data streams,
reserved Windows names, trailing-dot/space aliases, duplicate/case-colliding
names, NUL names, links/reparse points, encryption and unknown compression are
rejected before extraction. Limits are 4,096 entries, 128 MiB per file, 32 MiB
per JSON document and 1 GiB total/archive size. Extraction occurs in a fresh
private temporary directory; an existing destination is never reused and only
a completely verified payload becomes the destination. Inner NuGet ZIP entries
are also checked; nuspec DTD/entity declarations are forbidden.

The first job records the exact selection and all package hashes in its job
summary. The `publish` job then waits on the **fixed `release` environment**.
Before NuGet login it checks GitHub's actual approval history: there must be
one unambiguous approved review for `release` by the designated human maintainer
`marcschier` (immutable GitHub user ID `11168470`). The maintainer may explicitly
approve their own dispatch. An automatically created, unprotected environment,
absent approval, rejection, another user, bot or bypass without that review
cannot pass. Promotion reruns are forbidden; a fresh dispatch needs a fresh
approval. The complete tag/run/artifact/source selection is resolved and
verified again after the wait and compared with the pre-approval selection.

Only then does pinned `NuGet/login` exchange GitHub OIDC for its short-lived
`NUGET_API_KEY` output. The key is passed to the final process through step
environment, not stored in repository secrets, files or NuGet configuration.
The final helper rechecks approval, selection and attestation, verifies the
whole payload before the first push, and hashes each file immediately before
submission. It pushes those exact `.nupkg`/`.snupkg` files to NuGet's fixed HTTPS
v3 source, using an isolated empty `eng\release\nuget.config`. There is no rebuild,
repack, implicit symbol-file discovery, signing mutation, wildcard push or
`--skip-duplicate`.

NuGet publishing is not atomic across eleven package/symbol pairs. A timeout,
conflict or other failure stops immediately and reports possible partial remote
publication. Do not retag, overwrite, rebuild the version or silently skip
conflicts to recover. Inspect the exact submitted version and retained artifact
and approve a separate recovery decision. Server-side NuGet validation, malware
checks and symbol indexing remain asynchronous and are not certified by push
success. Repository-side NuGet signing may change subsequently downloaded bytes;
the same-byte guarantee here is for the files submitted by promotion.

GitHub Packages publication is deliberately **not implemented**. These workflows
request no `packages: write` permission and claim no GitHub Packages remote
setup. Adding another destination requires a separately approved integration
that consumes this same verified payload.

## Account-owner setup and remaining qualification

Before any real release, maintainers must separately authorize remote actions
and complete the implementation, semantic review and genuine native evidence.
All package and sample qualification statuses and every applicable native cell
must be backed by actual reviewed behavior, not edited merely to unblock CI.
The real source tree and both workflow files must exist on the authorized
repository's protected `main`; changing the version alone does not qualify it.

Configure GitHub branch/tag protections for `main` and release tags, protect
workflow and release-helper changes, and prohibit tag rewrites/deletion. Configure
the `release` environment with required reviewer `marcschier`, allow explicit
self-review, disable administrator bypass, and restrict deployment branches to
`main`. The account/repository plan must support environment approvals and
artifact attestations for its visibility. The workflow checks a real review
record but cannot create or administer those protections with its read-only
repository permissions.

Configure NuGet trusted publishing for repository owner `marcschier`, repository
`xregistry-dotnet`, workflow file **`nuget.yml`** (filename only), and environment
**`release`**. Scope the policy to the eleven approved IDs and the intended NuGet
owner, with permission to create first versions/IDs where needed. Configure the
environment variable `NUGET_USER` to the NuGet profile username, not an email.
No long-lived NuGet API token is needed or accepted by the workflow.

On nuget.org, sign into the `marcschier` profile, open the username menu and
choose **Trusted Publishing**, then add the GitHub policy with the values
above. Verify package ownership or first-publication rights before enabling
the policy's scopes. The exact IDs are:

```text
XRegistry
XRegistry.Client
XRegistry.Server
XRegistry.AspNetCore
XRegistry.Storage.File
XRegistry.Models
XRegistry.Validation
XRegistry.Federation
XRegistry.Bindings.File
XRegistry.Bindings.Git
XRegistry.Bindings.Oci
```

After qualification is complete, the later manual sequence is: create a matching
version tag, wait for the qualified attested `release` artifact, dispatch
`nuget.yml` from `main` with that exact version, inspect the selected hashes and
approve the `release` environment. Do not use the non-publishing CI artifacts
to bypass those checks. This initial repository setup does not perform that
publication sequence.

GitHub-hosted runners must provide current `gh` with the documented attestation
policy flags, Git, PowerShell, Python 3.13 and the SDK from `global.json` plus the
.NET 8 runtime/SDK. The pinned Node 24 actions require a sufficiently current
runner. Locked dependencies, all real native Windows/Linux x64/ARM64 execution
infrastructure, artifact retention and access to GitHub/NuGet/Sigstore services
remain integration requirements. Expired/deleted artifacts, unavailable
attestations, multiple matching successful runs or missing metadata are hard
blockers, not reasons to select another build.

No hosted workflow, OIDC exchange, approval protection, live artifact download,
real package build or NuGet publish has been qualified by local tooling tests.

## Local checks and official references

The narrow, offline tests run without credentials, network, native receipts or
solution builds:

```powershell
python -m unittest discover -s tests\Tooling -p 'test_release*.py' -v
```

Synthetic packages, receipts, GitHub responses, approvals and mocked push
processes exist only under test temporary directories. **Successful fixtures are
tooling tests, never release evidence.** The tests also preserve the workflow's
input/permission/action-pin contract. A GitHub Actions syntax checker can check
only these two new workflow files; it does not authorize executing them.

Action contracts were checked against official sources; checkout, SDK/Python
setup and artifact upload pins reuse this repository's existing CI pins.
The additional immutable commit pins are NuGet/login v1.2.0
`8d196754b4036150537f80ac539e15c2f1028841` and
actions/attest-build-provenance v3.2.0
`96278af6caaf10aea03fd8d33a09a777ca52d62f`, resolved to commits rather than
annotated tag-object IDs.

- [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
- [Pinned NuGet/login inputs and output](https://github.com/NuGet/login/blob/8d196754b4036150537f80ac539e15c2f1028841/action.yml)
- [Pinned provenance action](https://github.com/actions/attest-build-provenance/blob/96278af6caaf10aea03fd8d33a09a777ca52d62f/action.yml)
- [Artifact upload immutability and digest output](https://github.com/actions/upload-artifact/blob/b7c566a772e6b6bfb58ed0dc250532a479d7789f/README.md)
- [GitHub attestation verification policy flags](https://cli.github.com/manual/gh_attestation_verify)
- [Workflow runs and environment review history](https://docs.github.com/en/rest/actions/workflow-runs)
- [Artifact identity, digest and download API](https://docs.github.com/en/rest/actions/artifacts)
- [NuGet push and isolated configuration](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-push)
- [Explicit symbol-package publishing](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg)
