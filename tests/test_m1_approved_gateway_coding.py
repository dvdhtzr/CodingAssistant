"""M1.12 unit/contract tests: fake-only, never run model or host Podman."""
import builtins
import importlib.util
import json
import os
import pathlib
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "tools/m1"), str(ROOT / "tools/m0")]
spec = importlib.util.spec_from_file_location(
    "approved_gateway_coding", ROOT / "tools/m1/approved_gateway_coding.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
MOCK_BASE = "a" * 64
MOCK_IMAGE = "b" * 64
MOCK_DIGEST = "c" * 64
MOCK_MANIFEST = "d" * 64

def completed(code=0, out=""):
    return subprocess.CompletedProcess([], code, out, "")

class ApprovedGatewayCodingTests(unittest.TestCase):
    def test_strict_source_and_gateway_plugin_contract(self):
        source_hash = m.fingerprint()
        self.assertEqual(len(source_hash), 64)
        script = (m.PLUGIN / "index.mjs").read_text()
        self.assertIn('getAcpRuntimeBackend("acpx")', script)
        self.assertIn("runtime.ensureSession(", script)
        self.assertIn("runtime.startTurn(", script)
        self.assertIn('scope: "operator.write"', script)
        self.assertIn("claimed = true", script)
        self.assertIn("modelExplicit: true", script)
        self.assertIn('mode: "oneshot"', script)
        self.assertIn("await runtime.close(", script)
        self.assertIn('onElicitation: async () => ({ action: "cancel" })', script)
        self.assertNotIn("child_process", script)
        self.assertNotIn("session/prompt", script)
        self.assertNotIn("npx", script)
        config = json.loads(m.CONFIG.read_text())
        self.assertEqual(config["acp"]["allowedAgents"], ["opencode"])
        self.assertEqual(config["plugins"]["entries"]["acpx"]["config"]["cwd"], "/workspace")
        self.assertEqual(config["plugins"]["entries"]["acpx"]["config"]["mcpServers"], {})
        self.assertEqual(config["plugins"]["entries"]["acpx"]["config"]["nonInteractivePermissions"], "deny")
        self.assertNotIn("OPENCLAW_GATEWAY_TOKEN",
                         config["plugins"]["entries"]["acpx"]["config"]["agentProcessEnvAllow"])
        self.assertIn("OPENCLAW_GATEWAY_TOKEN",
                      config["plugins"]["entries"]["acpx"]["config"]["agentProcessEnvBlock"])

    def test_copy_only_offline_local_image_build_and_provenance(self):
        docker = m.CONTAINERFILE.read_text()
        self.assertIn("FROM "+"$"+"{BASE_IMAGE}", docker)
        for instruction in ("coding-plugin/", "coding-config.json", "coding-runner.mjs"):
            self.assertIn("COPY " + instruction, docker)
        self.assertNotIn("\nRUN ", docker)
        cmd = m.image_build_command(m.fingerprint())
        for expected in ("--pull=never", "--network=none",
                         "BASE_IMAGE=" + m.OWNER_IMAGE_ID):
            self.assertIn(expected, cmd)
        with self.assertRaises(ValueError):
            m.image_build_command("latest")
        labels = {m.LABEL + ".base": m.OWNER_IMAGE_ID,
                  m.LABEL + ".sources": m.fingerprint(),
                  m.LABEL + ".method": m.METHOD}
        item = [{"Id": "sha256:" + MOCK_IMAGE,
                 "Labels": labels, "RepoTags": [m.IMAGE_TAG]}]
        with mock.patch.object(m, "fixed", return_value=completed(out=json.dumps(item))):
            self.assertEqual(m.inspect_image(m.fingerprint()), MOCK_IMAGE)
        item[0]["Labels"] = {}
        with mock.patch.object(m, "fixed", return_value=completed(out=json.dumps(item))):
            with self.assertRaises(ValueError):
                m.inspect_image(m.fingerprint())

    def test_exact_approval_plan_changes_with_image_and_digest(self):
        plan, digest = m.build_approved_plan(MOCK_BASE, MOCK_IMAGE, MOCK_DIGEST,
                                             MOCK_MANIFEST, "f" * 64)
        self.assertEqual(len(digest), 64)
        self.assertEqual(plan["writable_files"], ["api/Program.cs"])
        self.assertEqual(plan["model"], "qwen3.5:4b")
        self.assertEqual(plan["evaluator_image_id"], "f" * 64)
        self.assertTrue(plan["no_cloud"] and plan["no_git_remote"])
        _, other = m.build_approved_plan(MOCK_BASE, MOCK_IMAGE, "e" * 64,
                                         MOCK_MANIFEST, "f" * 64)
        self.assertNotEqual(digest, other)
        with self.assertRaises(ValueError):
            m.build_approved_plan(MOCK_BASE, "latest", MOCK_DIGEST, MOCK_MANIFEST, "f" * 64)

    def test_rpc_exact_output_and_rejects_false_success(self):
        item = dict(m.EXPECTED_RPC, eventsObserved=8, toolCallsObserved=3)
        parsed = m.parse_rpc(json.dumps(item))
        self.assertTrue(parsed["turn_completed"])
        self.assertEqual(parsed["tool_calls"], 3)
        bad = (
            dict(item, turnCompleted=False),
            dict(item, promptSent="true"),
            dict(item, eventsObserved=0),
            dict(item, toolCallsObserved=99),
            dict(item, extra="hidden"),
            dict(item, modelInferenceClaim="confirmed"),
        )
        for value in bad:
            with self.subTest(value=value), self.assertRaises(ValueError):
                m.parse_rpc(json.dumps(value))
        for value in ("", "[]", "not-json"):
            with self.assertRaises(ValueError):
                m.parse_rpc(value)

    def test_container_mounts_only_disposable_copy_and_private_socket(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            workspace = root / "approved"
            workspace.mkdir()
            ipc = root / "ipc"
            ipc.mkdir()
            (ipc / "ollama.sock").write_bytes(b"stub-for-model-free-contract")
            env = root / "private.env"
            env.write_text("OPENCLAW_GATEWAY_TOKEN=TEST\n")
            fake = [
                "podman", "run", "--network=none", "--read-only",
                "--cap-drop=all", "--security-opt=no-new-privileges",
                "--pids-limit=96", "--memory=2g",
                "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
                "--entrypoint=node", MOCK_IMAGE,
                "/app/openclaw.mjs", "gateway"
            ]
            with mock.patch.object(m, "ephemeral_gateway", return_value=fake):
                result = m.container_command(
                    MOCK_IMAGE, "ca-m17-acpx-012345abcdef", env, workspace, ipc)
        for item in ("--network=none", "--read-only", "--cap-drop=all",
                     "--security-opt=no-new-privileges", "--memory=3g",
                     "--env=OPENCLAW_CONFIG_PATH=/app/codingassistant-coding-config.json"):
            self.assertIn(item, result)
        self.assertEqual(result[-1], "/app/codingassistant-coding-runner.mjs")
        self.assertIn(":/workspace:rw,nosuid,nodev", " ".join(result))
        self.assertIn(":/run/codingassistant:ro,nosuid,nodev", " ".join(result))
        self.assertNotIn("--publish", result)
        self.assertNotIn("--privileged", result)

    def test_rpc_request_only_fixed_method_and_plan_hash(self):
        name = "ca-m17-acpx-012345abcdef"
        args = m.gateway_rpc_command(name, MOCK_BASE)
        self.assertEqual(args[-7:], [
            "gateway", "call", m.METHOD,
            "--params", json.dumps({"planHash": MOCK_BASE}, separators=(",", ":")),
            "--json", "--timeout", "900000"][-7:])
        self.assertNotIn("agent", args)
        with self.assertRaises(ValueError):
            m.gateway_rpc_command(name, "not-a-plan")
        with self.assertRaises(ValueError):
            m.gateway_rpc_command("other", MOCK_BASE)

    def test_approval_prompt_displays_copyable_exact_line_and_stays_fail_closed(self):
        import contextlib
        import io
        output = io.StringIO()
        with contextlib.redirect_stdout(output), \
             mock.patch.object(builtins, "input", return_value="APPROVE"):
            self.assertFalse(m.request_exact_approval(MOCK_BASE))
        self.assertIn("APPROVE " + MOCK_BASE, output.getvalue())
        self.assertIn("FULL line", output.getvalue())
        with mock.patch.object(builtins, "input", return_value="APPROVE wrong"):
            self.assertFalse(m.request_exact_approval(MOCK_BASE))
        with mock.patch.object(builtins, "input",
                               return_value="APPROVE " + MOCK_BASE):
            self.assertTrue(m.request_exact_approval(MOCK_BASE))
        with mock.patch.object(builtins, "input", side_effect=EOFError):
            self.assertFalse(m.request_exact_approval(MOCK_BASE))
        with self.assertRaises(ValueError):
            m.request_exact_approval("invalid")

    def test_preflight_rejects_windows_without_starting_podman(self):
        with mock.patch.object(m.sys, "platform", "win32"), \
             mock.patch.object(m, "fixed") as runner:
            self.assertEqual(m.execute(False)["status"], "BLOCKED")
            runner.assert_not_called()

    @mock.patch.object(m.sys, "platform", "linux")
    @mock.patch.object(m.os, "geteuid", return_value=1000, create=True)
    def test_no_exact_approval_never_calls_gateway_or_model(self, *_):
        with mock.patch.object(m, "inspect_podman", return_value={"rootless": True}), \
             mock.patch.object(m, "verify_inputs", return_value=MOCK_BASE), \
             mock.patch.object(m, "inspect_composed", return_value=m.OWNER_IMAGE_ID), \
             mock.patch.object(m, "verify_combined", return_value={"registry": {
                 "id": "opencode", "installation": "installed",
                 "expectedCommand": True}}), \
             mock.patch.object(m, "check_manifest", return_value=MOCK_MANIFEST), \
             mock.patch.object(m, "sealed_snapshot", return_value=MOCK_BASE), \
             mock.patch.object(m, "image_status", return_value={
                 "present": True, "image_id": MOCK_IMAGE}), \
             mock.patch.object(m, "pin_model", return_value=MOCK_DIGEST), \
             mock.patch.object(m, "inspect_image", return_value=MOCK_IMAGE), \
             mock.patch.object(builtins, "input", return_value="APPROVE wrong-hash"), \
             mock.patch.object(m, "run_gateway") as gateway:
            result = m.execute(False)
        self.assertEqual(result["status"], "NOT_APPROVED", result)
        self.assertFalse(result["approved"])
        gateway.assert_not_called()

    @unittest.skipUnless(hasattr(socket, "AF_UNIX") and os.name == "posix",
                         "Unix socket broker requires POSIX")
    def test_broker_rejects_unapproved_methods_and_wrong_model_offline(self):
        with tempfile.TemporaryDirectory() as d:
            p = pathlib.Path(d)
            p.chmod(0o700)
            state = {"attempts": 0, "verified": 0, "failed": 0, "denied": 0}
            handler = m.broker_for("qwen3.5:4b", MOCK_DIGEST, state)
            with m.serve_bridge(p / "model.sock", handler):
                def request(raw: bytes) -> bytes:
                    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                    sock.settimeout(4)
                    sock.connect(str(p / "model.sock"))
                    try:
                        sock.sendall(raw)
                        chunks = []
                        while True:
                            part = sock.recv(32768)
                            if not part: break
                            chunks.append(part)
                        return b"".join(chunks)
                    finally:
                        sock.close()
                denied_get = request(b"GET /other HTTP/1.0\r\n\r\n")
                body = json.dumps({
                    "model": "cloud-model", "stream": True,
                    "messages": [{"role":"user","content":"probe"}]
                }).encode()
                denied_post = request(
                    b"POST /v1/chat/completions HTTP/1.0\r\n"
                    b"Content-Type: application/json\r\nContent-Length: " +
                    str(len(body)).encode() + b"\r\n\r\n" + body)
        self.assertIn(b"403", denied_get)
        self.assertIn(b"403", denied_post)
        self.assertEqual(state["attempts"], 0)
        self.assertEqual(state["denied"], 2)

if __name__ == "__main__":
    unittest.main()
