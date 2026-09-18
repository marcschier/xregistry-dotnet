# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Contract tests for the CRDT-aligned release pipeline: a tag-triggered GitHub
Packages publish job in packages.yml, and a workflow_dispatch nuget.yml that
promotes an already-published version from GitHub Packages to nuget.org.
These are offline text/structure checks; they do not execute the workflows or
contact GitHub, GitHub Packages or nuget.org."""

from __future__ import annotations

from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"
PACKAGE_IDS = (
    "XRegistry", "XRegistry.Client", "XRegistry.Server", "XRegistry.AspNetCore",
    "XRegistry.Storage.File", "XRegistry.Models", "XRegistry.Validation",
    "XRegistry.Federation", "XRegistry.Bindings.File", "XRegistry.Bindings.Git",
    "XRegistry.Bindings.Oci",
)
PINNED_ACTIONS = {
    "actions/checkout": "fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09",
    "actions/setup-python": "ece7cb06caefa5fff74198d8649806c4678c61a1",
    "actions/setup-dotnet": "26b0ec14cb23fa6904739307f278c14f94c95bf1",
    "actions/upload-artifact": "b7c566a772e6b6bfb58ed0dc250532a479d7789f",
    "NuGet/login": "8d196754b4036150537f80ac539e15c2f1028841",
}


def job_text(workflow: str, job: str) -> str:
    text = (WORKFLOWS / workflow).read_text(encoding="utf-8")
    match = re.search(rf"(?m)^  {re.escape(job)}:\n((?:    .*\n|\n)+)", text)
    assert match is not None, f"Job {job!r} not found in {workflow}"
    return match.group(1)


class PinnedActionTests(unittest.TestCase):
    def test_every_action_reference_in_release_workflows_is_an_immutable_pinned_commit(self) -> None:
        for name in ("packages.yml", "nuget.yml"):
            text = (WORKFLOWS / name).read_text(encoding="utf-8")
            actions = re.findall(r"uses: ([^@\s]+)@([^\s]+)", text)
            self.assertGreater(len(actions), 0)
            for action, pinned in actions:
                with self.subTest(workflow=name, action=action):
                    self.assertRegex(pinned, r"^[0-9a-f]{40}$")
                    self.assertIn(action, PINNED_ACTIONS)
                    self.assertEqual(pinned, PINNED_ACTIONS[action])


class PackagesWorkflowTests(unittest.TestCase):
    def setUp(self) -> None:
        self.text = (WORKFLOWS / "packages.yml").read_text(encoding="utf-8")

    def test_packages_job_runs_on_push_pr_and_tags_but_has_no_publish_authority(self) -> None:
        self.assertIn("tags: ['v*']", self.text)
        job = job_text("packages.yml", "packages")
        self.assertIn("eng/package_build.py", job)
        for forbidden in (
            "id-token: write", "packages: write", "contents: write", "secrets.",
            "NuGet/login", "dotnet nuget push",
        ):
            self.assertNotIn(forbidden, job)

    def test_publish_github_job_only_fires_on_tag_push_after_packages_succeeds(self) -> None:
        job = job_text("packages.yml", "publish-github")
        self.assertIn("needs: packages", job)
        self.assertIn("startsWith(github.ref, 'refs/tags/v')", job)
        self.assertIn("packages: write", job)
        self.assertNotIn("id-token: write", job)
        self.assertNotIn("environment:", job)

    def test_publish_github_job_packs_every_library_and_pushes_only_nupkg_with_skip_duplicate(self) -> None:
        job = job_text("packages.yml", "publish-github")
        for package_id in PACKAGE_IDS:
            self.assertIn(f"src/{package_id}/{package_id}.csproj", job)
        self.assertIn('dotnet nuget push "artifacts/publish/*.nupkg"', job)
        self.assertIn("https://nuget.pkg.github.com/marcschier/index.json", job)
        self.assertIn("--api-key ${{ secrets.GITHUB_TOKEN }}", job)
        self.assertIn("--skip-duplicate", job)
        self.assertNotIn(".snupkg", job)
        self.assertNotIn("python", job)

    def test_no_custom_python_release_orchestrator_remains(self) -> None:
        self.assertNotIn("release.py", self.text)
        self.assertFalse((ROOT / "eng" / "release").exists())
        self.assertFalse((WORKFLOWS / "release.yml").exists())


class NuGetWorkflowTests(unittest.TestCase):
    def setUp(self) -> None:
        self.text = (WORKFLOWS / "nuget.yml").read_text(encoding="utf-8")

    def test_dispatch_only_with_optional_blank_default_version(self) -> None:
        self.assertNotIn("pull_request", self.text)
        self.assertNotIn("push:", self.text)
        self.assertRegex(self.text, r"workflow_dispatch:\s+inputs:\s+version:")
        self.assertIn("required: false", self.text)
        self.assertIn("default: ''", self.text)

    def test_publish_job_uses_the_release_environment_and_minimal_oidc_permissions(self) -> None:
        job = job_text("nuget.yml", "publish")
        self.assertIn("environment: release", job)
        self.assertIn("id-token: write", job)
        self.assertIn("packages: read", job)
        self.assertNotIn("packages: write", job)
        self.assertNotIn("contents: write", job)

    def test_downloads_every_package_from_github_packages_then_promotes_to_nuget_org(self) -> None:
        job = job_text("nuget.yml", "publish")
        self.assertIn("nuget.pkg.github.com/marcschier/index.json", job)
        for package_id in PACKAGE_IDS:
            self.assertIn(package_id, job)
        self.assertIn("uses: NuGet/login@", job)
        self.assertIn("user: ${{ vars.NUGET_USER }}", job)
        self.assertIn('dotnet nuget push "./download/*.nupkg"', job)
        self.assertIn("--api-key ${{ steps.login.outputs.NUGET_API_KEY }}", job)
        self.assertIn("https://api.nuget.org/v3/index.json", job)
        self.assertIn("--skip-duplicate", job)

    def test_verifies_nuget_user_is_resolved_before_login_to_surface_misconfiguration_early(
        self,
    ) -> None:
        # A NUGET_USER mismatch (e.g. an environment-scoped variable silently
        # shadowing the repository-level one) previously only surfaced as an
        # opaque 401 from nuget.org. Guard against regressing that: the
        # workflow must fail fast with a clear message and print the resolved
        # (non-secret) value before the login step ever runs.
        job = job_text("nuget.yml", "publish")
        verify_index = job.index("Verify NUGET_USER")
        login_index = job.index("NuGet login (OIDC trusted publishing)")
        self.assertLess(verify_index, login_index)
        self.assertIn("vars.NUGET_USER is empty", job)
        self.assertIn("Resolved NUGET_USER", job)

    def test_no_custom_python_release_orchestrator_remains(self) -> None:
        self.assertNotIn("release.py", self.text)
        self.assertNotIn("python", self.text)
        self.assertNotIn("secrets.NUGET", self.text)


if __name__ == "__main__":
    unittest.main()
