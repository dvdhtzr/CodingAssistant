"""Model-free tests for the OpenCode/Ollama discovery probe."""
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "tools" / "m0"))
from opencode_probe import analyze, model_ids


class OpenCodeDiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.tags = {"models": [
            {"name": "qwen3.5:4b", "digest": "sha256:example"},
            {"name": "nomic-embed-text:v1.5", "digest": "sha256:embedding"},
        ]}
        self.details = {"capabilities": ["completion", "tools"]}

    def test_parse_exact_model_ids(self):
        self.assertEqual(model_ids("ollama/qwen3.5:4b\nopenai/gpt-5\n"),
                         {"ollama/qwen3.5:4b", "openai/gpt-5"})

    def test_parse_ignores_labels_and_ansi(self):
        self.assertEqual(model_ids("\x1b[32mollama/qwen3.5:4b\x1b[0m\nname ollama/qwen3.5:4b\n"),
                         {"ollama/qwen3.5:4b"})

    def test_exact_local_discovery_passes(self):
        report = analyze("qwen3.5:4b", self.tags, self.details,
                         "ollama/qwen3.5:4b\n", 0)
        self.assertEqual(report["status"], "PASS")
        self.assertEqual(report["model_digest"], "sha256:example")

    def test_cloud_model_cannot_substitute_for_local(self):
        report = analyze("qwen3.5:4b", self.tags, self.details,
                         "openai/qwen3.5:4b\n", 0)
        self.assertEqual(report["status"], "BLOCKED")

    def test_near_match_does_not_pass(self):
        report = analyze("qwen3.5:4b", self.tags, self.details,
                         "ollama/qwen3.5:4b-bigger\n", 0)
        self.assertEqual(report["status"], "BLOCKED")

    def test_missing_tool_support_blocks(self):
        report = analyze("qwen3.5:4b", self.tags,
                         {"capabilities": ["completion"]}, "ollama/qwen3.5:4b\n", 0)
        self.assertEqual(report["status"], "BLOCKED")

    def test_cli_error_blocks(self):
        report = analyze("qwen3.5:4b", self.tags, self.details, "ollama/qwen3.5:4b\n", 1)
        self.assertEqual(report["status"], "BLOCKED")

    def test_digest_missing_blocks(self):
        report = analyze("qwen3.5:4b", {"models": [{"name": "qwen3.5:4b"}]},
                         self.details, "ollama/qwen3.5:4b\n", 0)
        self.assertEqual(report["status"], "BLOCKED")


if __name__ == "__main__":
    unittest.main()
