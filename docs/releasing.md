# Releasing xRegistry .NET

The source version is `0.1.0-alpha`. Packages and samples are implemented but
not release-qualified; incomplete native/conformance requirements are not
marked qualified merely to publish an early alpha, and publishing does not by
itself certify runtime/specification qualification. Publishing this alpha does
not itself claim conformance: NuGet.org visibly marks a prerelease version, and
that exact version string can never be reused by a later stable/`rc` release.

The pipeline structure mirrors this account's other .NET repositories
(for example `marcschier/crdt`): a tag push builds and publishes packages to
the repository's own GitHub Packages feed, and a separate, manually dispatched
workflow promotes an already-published version from GitHub Packages to
nuget.org using NuGet trusted publishing (OIDC). There is no separate
attestation/approval-gated release workflow, no custom Python release
orchestrator and no artifact resolution step; the two GitHub Actions workflows
below use the same plain `dotnet`/shell commands a maintainer would run
locally.

## Ordinary CI package validation

`packages.yml`'s `packages` job runs on every push to `main`, every pull
request, and every `v*` tag push. It performs a locked `eng\build.ps1` build,
then `eng\package_build.py`, which packs the exact source version (from
`version.json`) for all eleven library projects and verifies the resulting
`.nupkg`/`.snupkg` pairs (repository URL, commit, `net8.0`/`net10.0` library
assets). The build output is uploaded as a non-publishing CI artifact. This job
has only `contents: read`; it has no publishing token, no OIDC permission and
never runs `dotnet nuget push`.

## Tag-triggered GitHub Packages publication

`packages.yml`'s `publish-github` job runs only for pushes to a `v*` tag,
after the `packages` validation job has passed. It has `packages: write` and
`contents: read`, and nothing else:

```yaml
- run: dotnet pack src/XRegistry/XRegistry.csproj -c Release -o artifacts/publish
  # ... one dotnet pack per library project ...
- run: dotnet nuget push "artifacts/publish/*.nupkg" \
    --source https://nuget.pkg.github.com/marcschier/index.json \
    --api-key ${{ secrets.GITHUB_TOKEN }} --skip-duplicate
```

Nerdbank.GitVersioning computes the package version directly from the pushed
tag (`version.json`'s `publicReleaseRefSpec` already matches `refs/tags/v<semver>`,
including a `-alpha`/`-alpha.N` suffix), so no explicit `-p:PackageVersion=`
override is needed. GitHub Packages' NuGet registry does not accept `.snupkg`
symbol packages, so only the `.nupkg` files are pushed; this matches this
account's other repositories and is a GitHub platform limitation, not an
omission. `--skip-duplicate` allows a rerun of a partially-failed publish
without erroring on packages that already made it through.

## Promoting a published version to nuget.org

`nuget.yml` is `workflow_dispatch`-only, with one optional `version` input
(blank promotes the latest version already published to GitHub Packages). The
single `publish` job runs on `ubuntu-latest` under the `release` environment,
with `id-token: write` (for NuGet trusted publishing OIDC), `contents: read`
and `packages: read`:

1. Resolve the GitHub Packages NuGet v3 feed's package-content base address,
   then download the exact (or latest) `.nupkg` for each of the eleven package
   IDs using `curl`/`jq` and the job's own `GITHUB_TOKEN`.
2. Exchange the `release` environment's OIDC token for a short-lived NuGet API
   key via the pinned `NuGet/login` action, using the configured `NUGET_USER`
   trusted-publishing account name.
3. `dotnet nuget push "./download/*.nupkg" --api-key <short-lived key> --source
   https://api.nuget.org/v3/index.json --skip-duplicate`.

Because this job requires the `release` deployment environment, GitHub enforces
that environment's configured protection rules (required reviewers, allowed
branches) before the job starts; no separate custom approval logic is needed in
the workflow itself. NuGet publication is not atomic across the eleven
packages: a failure partway through can leave a partial publish. `--skip-duplicate`
makes a rerun of the same dispatch safe; inspect nuget.org before retrying with
a different version.

## Account-owner setup

Configure GitHub branch/tag protections for `main` and release tags, and
prohibit tag rewrites/deletion. Configure the `release` environment with the
required reviewer(s) and restrict deployment branches to `main`.

Configure NuGet trusted publishing for GitHub repository owner `marcschier`,
repository `xregistry-dotnet`, workflow file **`nuget.yml`** (filename only),
and environment **`release`**. Scope the policy to the eleven package IDs
below, with permission to create first versions/IDs where needed. Trusted
publishing policies are created under a NuGet.org **package owner** account,
which is *not necessarily* the same name as the GitHub repository owner (for
example the policy may be created under a personal nuget.org account like
`mschier` even though the GitHub repo is owned by `marcschier`). The
`NUGET_USER` value below must always be **the nuget.org policy-creator
username shown on the Trusted Publishing policy page**, not the GitHub
username — a mismatch here fails at runtime with `Token exchange failed
(HTTP 401)` and `No matching trust policy owned by user '<name>' was found`.

Set `NUGET_USER` as a **repository-level** Actions variable, not an
environment-level one:

```powershell
gh variable set NUGET_USER --body "<nuget.org policy-creator username>"
```

Do **not** also create an environment-scoped `NUGET_USER` (`--env release`)
for the same name. GitHub Actions resolves `vars.NUGET_USER` from the
environment first and silently falls back to the repository-level value only
when no environment-level variable exists; a stale or incorrect
environment-level value added later would silently shadow a correct
repository-level one with no error until the next promotion run fails. Keep
the single repository-level variable as the one source of truth. The
`Verify NUGET_USER` step in `nuget.yml` prints the resolved (non-secret)
username before login specifically so any future mismatch is visible in the
run logs immediately, instead of surfacing only as an opaque 401. No
long-lived NuGet API token is needed or accepted by the workflow.

On nuget.org, sign into the account that owns (or will own) the eleven
package IDs, open the username menu and choose **Trusted Publishing**, then
add the GitHub policy with the values above. Verify package ownership or
first-publication rights before enabling the policy's scopes. The exact IDs
are:

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

GitHub Packages publication of this repository's own packages needs no
additional setup beyond the `publish-github` job's `packages: write`
permission and the workflow's own `GITHUB_TOKEN`; it does not require a
personal access token or an environment.

## Alpha prerelease exception

`eng\pack.ps1 -Release` (a local, maintainer-run packing check; not part of
either GitHub Actions workflow above) normally requires
`python eng\check_packages.py --release` and
`python eng\specification\manage.py release` to confirm every package/sample
is `qualified` and every applicable specification requirement is reviewed with
full native execution evidence. This is a large, ongoing body of work, and this
early `0.1.0-alpha` release is explicitly published without claiming it is
complete.

**Scope.** The exception applies only when `version.json`'s `version` field is
an explicit `alpha` prerelease (for example `0.1.0-alpha` or `0.1.0-alpha.3`).
A stable version or any other prerelease channel (`rc`, `beta`, and so on)
always uses the full strict check with no exception.

**What is relaxed.** `check_packages.py --release` does not require every
package/sample `status` to be `qualified`; `specification\manage.py release`
does not require `semanticCoverageReviewed: true` or per-requirement native
evidence. Both print an explicit `ALPHA PRERELEASE EXCEPTION` notice when this
applies. No package or specification row's persisted `status` is edited to say
`qualified` when it is not; the ledger continues to truthfully record its
actual `implemented`/`pending` state.

## Local checks

The narrow, offline tests run without credentials, network, or a solution
build:

```powershell
python -m unittest discover -s tests\Tooling -p "test_*release*.py" -v
python -m unittest discover -s tests\Tooling -p test_package_build.py -v
```

These validate the workflow contract (permissions, pinned actions, no stray
publishing authority outside the tag-triggered job) and `eng\package_build.py`'s
package verification. They do not execute the workflows or contact GitHub,
GitHub Packages or nuget.org.

- [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
- [Working with the NuGet registry (GitHub Packages)](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
- [Pinned NuGet/login inputs and output](https://github.com/NuGet/login/blob/8d196754b4036150537f80ac539e15c2f1028841/action.yml)
- [NuGet push](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-push)
