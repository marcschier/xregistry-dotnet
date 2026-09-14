from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]


class WorkflowContractTests(unittest.TestCase):
    def test_shell_commands_with_colon_space_use_block_or_quoted_yaml(self):
        for path in (ROOT / ".github" / "workflows").glob("*.yml"):
            for number, line in enumerate(path.read_text().splitlines(), 1):
                text = line.strip()
                if text.startswith("run: "):
                    command = text[5:]
                    if not command.startswith(("|", ">", "'", '"')):
                        self.assertNotIn(": ", command, f"{path.name}:{number}")

    def test_model_probe_success_does_not_leak_its_expected_jit_exit_code(self):
        text = (ROOT / "eng" / "test-model-packages.ps1").read_text()
        self.assertIn("if ($LASTEXITCODE -ne 2)", text)
        self.assertGreater(text.index("$global:LASTEXITCODE = 0"), text.index("if ($LASTEXITCODE -ne 2)"))

    def test_bridge_uses_one_dotnet_application_when_path_has_multiple_matches(self):
        text = (ROOT / "samples" / "XRegistry.FederationBridge" / "verify-native.ps1").read_text()
        self.assertIn("Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1", text)


if __name__ == "__main__":
    unittest.main()
