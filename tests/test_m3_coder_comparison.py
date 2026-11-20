"""No-network unit checks for the isolated local Coder comparison probe."""
import importlib.util
import pathlib
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("coder_comparison",
                                              ROOT / "tools/m3/compare_coder.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class CoderComparisonContracts(unittest.TestCase):
    def test_exact_model_allowlist(self):
        with self.assertRaises(probe.ProbeBlocked):
            probe.run_once("remote/provider:latest", 30)

    def test_tool_call_has_to_be_exact_and_correct(self):
        valid = {"message": {"tool_calls": [{
            "function": {"name": "submit_repair", "arguments": {
                "source": "public static int Add(int left, int right) "
                          "{ return left + right; }"
            }}
        }]}}
        self.assertEqual(probe.extract_patch(valid), (True, True))
        invalid = {"message": {"tool_calls": [{
            "function": {"name": "submit_repair", "arguments": {
                "source": "public static int Add(int left, int right) "
                          "{ return left - right; }"
            }}
        }]}}
        self.assertEqual(probe.extract_patch(invalid), (True, False))
        self.assertEqual(probe.extract_patch({"message": {"content": "hello"}}),
                         (False, False))

    def test_comparison_uses_local_only_tool_call(self):
        reply = {"done": True, "eval_count": 32, "eval_duration": 2_000_000_000,
                 "message": {"tool_calls": [{
                     "function": {"name": "submit_repair", "arguments": {
                         "source": "public static int Add(int left, int right) "
                                   "{ return left + right; }"
                     }}
                 }]}}
        with mock.patch.object(probe, "local_request", return_value=reply) as call:
            result = probe.run_once(probe.CODER, 30)
        self.assertTrue(result["tool_call"])
        self.assertTrue(result["simple_repair"])
        self.assertEqual(result["tokens_per_second"], 16)
        path, request = call.call_args.args
        self.assertEqual(path, "/api/chat")
        self.assertEqual(request["model"], probe.CODER)
        self.assertFalse(request["stream"])
        self.assertEqual(request["tools"][0]["function"]["name"], "submit_repair")

    def test_arbitrary_remote_route_refused_before_network(self):
        with self.assertRaises(probe.ProbeBlocked):
            probe.local_request("https://ollama.com/anything")

    def test_ollama_pull_is_only_explicit_and_fixed(self):
        with mock.patch.object(probe.subprocess, "run",
                               return_value=mock.Mock(returncode=0)) as run:
            probe.pull_coder()
        self.assertEqual(run.call_args.args[0],
                         ["ollama", "pull", "qwen2.5-coder:3b"])
        self.assertEqual(run.call_args.kwargs["stdin"],
                         probe.subprocess.DEVNULL)


if __name__ == "__main__":
    unittest.main()
