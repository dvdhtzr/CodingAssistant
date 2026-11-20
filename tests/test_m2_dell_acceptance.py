"""Model-free Dell acceptance driver tests: never launch Podman/API/LLM in CI."""
import contextlib
import importlib.util
import io
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "dell_acceptance", ROOT / "tools/m2/dell_acceptance.py")
dell = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dell)


class DellAcceptanceContract(unittest.TestCase):
    def test_cli_requires_mode_and_preflight_does_not_run_model(self):
        with mock.patch.object(sys, "argv", ["acceptance", "--preflight-only"]), \
             mock.patch.object(dell, "preflight") as qualified, \
             mock.patch.object(dell, "perform_run",
                               side_effect=AssertionError("model executed")), \
             contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(dell.main(), 0)
            qualified.assert_called_once_with()

    def test_exact_owner_approval_mismatch_is_denied_without_start(self):
        run_id = "a" * 32
        control = "c" * 64
        execution = "d" * 64
        plan = {
            "fixture": "02-build-repair",
            "model": "qwen3.5:4b",
            "model_digest": "f" * 64,
            "writable_files": ["api/Program.cs"],
            "image_id": "b" * 64,
            "evaluator_image_id": "e" * 64,
            "no_cloud": True, "no_git_remote": True,
            "control": {"run_id": run_id, "plan_hash": control},
        }
        client = mock.Mock()
        client.call.return_value = {
            "planHash": execution, "state": "Previewed", "plan": plan
        }
        with mock.patch("builtins.input", return_value="APPROVE " + control), \
             contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(dell.AcceptanceBlocked):
                dell.exact_owner_approval(client, run_id, control)
        self.assertEqual(client.call.call_count, 1)
        self.assertEqual(client.call.call_args.args[:2],
                         ("POST", "/api/runs/" + run_id + "/execution-plan"))

    def test_owner_approval_is_exact_but_does_not_auto_start(self):
        run_id, m2_hash, m1_hash = "a" * 32, "c" * 64, "d" * 64
        client = mock.Mock()
        client.call.side_effect = [
            {"planHash": m1_hash, "state": "Previewed", "plan": {
                "fixture": "02-build-repair", "model": "qwen3.5:4b",
                "writable_files": ["api/Program.cs"],
                "no_cloud": True, "no_git_remote": True,
                "control": {"run_id": run_id, "plan_hash": m2_hash},
                "model_digest": "e" * 64,
                "image_id": "f" * 64, "evaluator_image_id": "b" * 64,
            }},
            {"state": "Authorized", "planHash": m1_hash},
        ]
        with mock.patch("builtins.input", return_value="APPROVE " + m1_hash), \
             contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(
                dell.exact_owner_approval(client, run_id, m2_hash), m1_hash)
        self.assertEqual(client.call.call_count, 2)
        self.assertEqual(client.call.call_args.args[1],
                         f"/api/runs/{run_id}/execution-authorization")
        self.assertFalse(any("/start" in str(x) for x in client.call.call_args_list))

    def test_refuse_unexpected_scope_even_if_hash_and_input_are_correct(self):
        run_id, m2_hash, m1_hash = "a" * 32, "c" * 64, "d" * 64
        def fake(scope):
            return mock.Mock(call=mock.Mock(return_value={
                "planHash": m1_hash, "state": "Previewed",
                "plan": {
                    "fixture": "02-build-repair", "model": "qwen3.5:4b",
                    "writable_files": scope,
                    "no_cloud": True, "no_git_remote": True,
                    "control": {"run_id": run_id, "plan_hash": m2_hash},
                }
            }))
        with mock.patch("builtins.input",
                        side_effect=AssertionError("untrusted preview prompted")):
            for scope in (
                ["../../home/owner/.ssh/id_rsa"],
                ["/etc/passwd"],
                ["api/Program.cs", "api/Second.cs"],
                ["api/link-to-host"],
            ):
                with self.subTest(scope=scope):
                    with self.assertRaises(dell.AcceptanceBlocked):
                        dell.exact_owner_approval(fake(scope), run_id, m2_hash)

    def test_local_api_client_never_uses_ambient_proxy(self):
        client = dell.ApiClient(5098, "f" * 64)
        self.assertEqual(client.origin, "http://127.0.0.1:5098")
        proxies = [handler for handler in client.opener.handlers
                   if isinstance(handler, dell.urllib.request.ProxyHandler)]
        self.assertEqual(len(proxies), 1)
        self.assertEqual(proxies[0].proxies, {})
        with self.assertRaises(dell.AcceptanceBlocked):
            client.call("GET", "https://example.com")
        with self.assertRaises(dell.AcceptanceBlocked):
            client.call("GET", "/api/../../secrets")

    def test_missing_self_contained_binary_cannot_start_server_or_model(self):
        with tempfile.TemporaryDirectory() as temp:
            with mock.patch.object(dell.subprocess, "Popen",
                                   side_effect=AssertionError("server launched")):
                with self.assertRaises(dell.AcceptanceBlocked):
                    dell.perform_run(pathlib.Path(temp) / "not-installed")

if __name__ == "__main__":
    unittest.main()
