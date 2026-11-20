"""Model-free security tests for task-specific read-only Gateway contract mounts."""
import hashlib
import importlib.util
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "tools/m1"), str(ROOT / "tools/m0")]
spec = importlib.util.spec_from_file_location(
    "approved_gateway_coding_task_mount", ROOT / "tools/m1/approved_gateway_coding.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
IMAGE = "a" * 64


class TaskMountTests(unittest.TestCase):
    def test_task_mount_is_readonly_and_requires_exact_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            workspace = root / "workspace"
            workspace.mkdir()
            ipc = root / "ipc"
            ipc.mkdir()
            (ipc / "ollama.sock").write_bytes(b"mock-socket")
            contract = root / "contract"
            contract.mkdir()
            task = contract / "task.json"
            task.write_bytes(b'{"schema":"test"}')
            digest = hashlib.sha256(task.read_bytes()).hexdigest()
            env = root / "private.env"
            env.write_text("TEST=YES\n")
            fake = [
                "podman", "run", "--network=none", "--read-only",
                "--cap-drop=all", "--security-opt=no-new-privileges",
                "--pids-limit=96", "--memory=2g",
                "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
                "--entrypoint=node", IMAGE, "/app/openclaw.mjs", "gateway"
            ]
            with mock.patch.object(m, "ephemeral_gateway", return_value=fake):
                args = m.container_command(
                    IMAGE, "ca-m17-acpx-012345abcdef", env, workspace, ipc,
                    task_contract_dir=contract, task_contract_sha256=digest)
                self.assertIn(str(contract.resolve()) +
                    ":/run/codingassistant-task:ro,nosuid,nodev", args)
                self.assertIn(str(workspace.resolve()) +
                    ":/workspace:rw,nosuid,nodev", args)
                self.assertNotIn("--privileged", args)
                self.assertNotIn("--publish", args)
                for bad in (None, "invalid", "0" * 64):
                    with self.assertRaises(ValueError):
                        m.container_command(
                            IMAGE, "ca-m17-acpx-012345abcdef", env, workspace, ipc,
                            task_contract_dir=contract, task_contract_sha256=bad)
                task.write_bytes(b'{"schema":"tampered"}')
                with self.assertRaises(ValueError):
                    m.container_command(
                        IMAGE, "ca-m17-acpx-012345abcdef", env, workspace, ipc,
                        task_contract_dir=contract, task_contract_sha256=digest)
                task.unlink()
                task.symlink_to(root / "private.env")
                with self.assertRaises(ValueError):
                    m.container_command(
                        IMAGE, "ca-m17-acpx-012345abcdef", env, workspace, ipc,
                        task_contract_dir=contract, task_contract_sha256=digest)

    def test_default_fixture_mode_requires_no_new_mount(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            workspace = root / "workspace"
            workspace.mkdir()
            ipc = root / "ipc"
            ipc.mkdir()
            (ipc / "ollama.sock").write_bytes(b"mock")
            env = root / "private.env"
            env.write_text("TEST=YES\n")
            fake = [
                "podman", "run", "--network=none", "--read-only",
                "--cap-drop=all", "--security-opt=no-new-privileges",
                "--pids-limit=96", "--memory=2g",
                "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
                "--entrypoint=node", IMAGE, "/app/openclaw.mjs", "gateway"
            ]
            with mock.patch.object(m, "ephemeral_gateway", return_value=fake):
                args = m.container_command(
                    IMAGE, "ca-m17-acpx-012345abcdef", env, workspace, ipc)
                self.assertNotIn(":/run/codingassistant-task:ro", " ".join(args))


if __name__ == "__main__":
    unittest.main()
