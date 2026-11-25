"""No-model CI contracts for the opt-in, real Dell orphan recovery probe."""
import os
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m1"))
import real_recovery_probe as p


class RealRecoveryProbeContracts(unittest.TestCase):
    def test_disposable_guest_is_pinned_and_strictly_isolated(self):
        rid = "a" * 24
        command = p.probe_command(p.OWNER_IMAGE_ID, rid, 1000, 1000)
        self.assertEqual(command[0:3], ["podman", "run", "--detach"])
        for flag in (
            "--rm", "--pull=never", "--network=none", "--read-only",
            "--cap-drop=all", "--security-opt=no-new-privileges",
            "--userns=keep-id", "--user=1000:1000",
            "--pids-limit=256", "--memory=3g", "--cpus=2",
            "--no-healthcheck",
            "--tmpfs=/tmp:rw,nosuid,nodev,size=128m,notmpcopyup",
            "--env=HOME=/tmp", "--workdir=/tmp", "--entrypoint=node",
        ):
            with self.subTest(flag=flag):
                self.assertIn(flag, command)
        self.assertEqual(command[command.index("--label") + 1],
                         p.LABEL + "=" + rid)
        self.assertEqual(command[command.index("--name") + 1],
                         p.managed_container(rid))
        self.assertEqual(command[command.index("--entrypoint=node") + 1],
                         p.OWNER_IMAGE_ID)
        self.assertEqual(command[-2:], ["-e", "setInterval(() => {}, 1000)"])
        for prohibited in (
            "--privileged", "--publish", "-p", "--volume", "-v",
            "--mount", "--network=host", "--security-opt=label=disable",
            "--env-file", "ollama", "opencode", "curl", "bash"):
            with self.subTest(prohibited=prohibited):
                self.assertNotIn(prohibited, command)

    def test_unapproved_container_id_and_host_root_rejected(self):
        bad = [
            ("b" * 64, "a" * 24, 1000, 1000),
            (p.OWNER_IMAGE_ID, "abc", 1000, 1000),
            (p.OWNER_IMAGE_ID, "../user", 1000, 1000),
            (p.OWNER_IMAGE_ID, "A" * 24, 1000, 1000),
            (p.OWNER_IMAGE_ID, "a" * 24, 0, 1000),
            (p.OWNER_IMAGE_ID, "a" * 24, 1000, 0),
        ]
        for value in bad:
            with self.subTest(value=value), self.assertRaises(ValueError):
                p.probe_command(*value)

    def test_label_inspection_requires_matching_active_exact_container(self):
        rid = "a" * 24
        with mock.patch.object(p, "fixed", side_effect=[
            p.subprocess.CompletedProcess([], 0, rid + "\n", ""),
            p.subprocess.CompletedProcess([], 0, "true\n", ""),
        ]) as runner:
            self.assertTrue(p._verify_guest(rid))
        commands = [x.args[0] for x in runner.call_args_list]
        self.assertEqual(len(commands), 2)
        self.assertEqual(commands[0][1:3], ["inspect", "--format"])
        self.assertIn(p.LABEL, commands[0][3])
        self.assertEqual(commands[0][-1], p.managed_container(rid))
        self.assertEqual(commands[1][-1], p.managed_container(rid))

        for label, running, code in (
            ("another-run-id\n", "true\n", 0),
            (rid + "\n", "false\n", 0),
            (rid + "\n", "true\n", 1),
        ):
            with self.subTest(label=label, running=running, code=code), \
                 mock.patch.object(p, "fixed", side_effect=[
                     p.subprocess.CompletedProcess([], code, label, ""),
                     p.subprocess.CompletedProcess([], 0, running, ""),
                 ]):
                self.assertFalse(p._verify_guest(rid))

    def test_readiness_token_must_be_one_bounded_exact_lowercase_id(self):
        valid = "READY " + "0" * 24 + "\n"
        self.assertIsNotNone(p.READY.fullmatch(valid))
        for message in (
            "READY " + "0" * 23 + "\n",
            "READY " + "G" * 24 + "\n",
            "READY " + "0" * 24 + "\nSECRET",
            "READY " + "0" * 24 + "\n\n",
            "../../workspace\n",
            "",
        ):
            with self.subTest(message=message):
                self.assertIsNone(p.READY.fullmatch(message))
        self.assertEqual(p.CHILD_EXIT, 73)

    def test_windows_and_root_will_never_start_a_guest(self):
        with mock.patch.object(p.sys, "platform", "win32"), \
             mock.patch.object(p, "inspect_podman") as runtime, \
             mock.patch.object(p.subprocess, "Popen") as spawn:
            result = p.run_probe()
            self.assertEqual(result["status"], "BLOCKED")
            self.assertEqual(result["reason"], "PLATFORM")
            self.assertFalse(result["model_inference"])
            runtime.assert_not_called()
            spawn.assert_not_called()
        if os.name == "posix":
            with mock.patch.object(p.sys, "platform", "linux"), \
                 mock.patch.object(p.os, "geteuid", return_value=0, create=True), \
                 mock.patch.object(p, "inspect_podman") as runtime, \
                 mock.patch.object(p.subprocess, "Popen") as spawn:
                self.assertEqual(p.run_probe()["reason"], "PLATFORM")
                runtime.assert_not_called()
                spawn.assert_not_called()

    def test_rootless_and_pinned_image_check_fail_before_launch(self):
        with mock.patch.object(p.sys, "platform", "linux"), \
             mock.patch.object(p.os, "geteuid", return_value=1000, create=True), \
             mock.patch.object(p, "inspect_podman",
                               return_value={"rootless": False}), \
             mock.patch.object(p.subprocess, "Popen") as spawn:
            result = p.run_probe()
            self.assertEqual(result["reason"], "ROOTLESS")
            spawn.assert_not_called()
        with mock.patch.object(p.sys, "platform", "linux"), \
             mock.patch.object(p.os, "geteuid", return_value=1000, create=True), \
             mock.patch.object(p, "inspect_podman",
                               return_value={"rootless": True}), \
             mock.patch.object(p, "fixed",
                               return_value=p.subprocess.CompletedProcess(
                                   [], 1, "image absent", "")), \
             mock.patch.object(p.subprocess, "Popen") as spawn:
            result = p.run_probe()
            self.assertEqual(result["reason"], "IMAGE_MISSING")
            spawn.assert_not_called()

    def test_probe_cannot_emit_real_success_when_environment_incomplete(self):
        self.assertFalse(p.TEST_PLAN == "0349c4327de288058ca556289e09839235db3e84bc699e06b1fbd0582dc5cd06")
        self.assertEqual(len(p.REQUEST_ID), 32)
        self.assertEqual(len(p.TEST_PLAN), 64)
        self.assertNotIn("--network=host", p.probe_command(
            p.OWNER_IMAGE_ID, "f" * 24, 1000, 1000))
        with mock.patch.object(p.sys, "platform", "linux"), \
             mock.patch.object(p.os, "geteuid", return_value=1000, create=True), \
             mock.patch.object(p, "inspect_podman", side_effect=OSError("SECRET")):
            result = p.run_probe()
        self.assertEqual(result["status"], "BLOCKED")
        self.assertEqual(result["reason"], "OSError")
        self.assertNotIn("SECRET", str(result))
        self.assertFalse(result["model_inference"])
        self.assertFalse(result["actual_owner_approval"])

    @unittest.skipUnless(sys.platform == "linux", "Linux private-lock semantics")
    def test_retained_probe_rejects_wrong_label_and_active_then_retries(self):
        if os.geteuid() == 0:
            self.skipTest("nonroot host required")
        with tempfile.TemporaryDirectory() as temp, \
             mock.patch.object(p, "ARTIFACTS", pathlib.Path(temp)), \
             mock.patch.object(p, "inspect_podman", return_value={"rootless": True}):
            report = {}
            with p.retained_ledger(report) as folder:
                with p.RunLedger(folder / "private-ledger") as ledger:
                    handle = ledger.start(p.REQUEST_ID)
                    handle.advance("PREFLIGHT")
                    handle.advance("AWAITING_APPROVAL", plan_hash=p.TEST_PLAN)
                    handle.advance("APPROVED")
                    handle.advance("RUNNING", container_name=handle.container_name())
                    rid = handle.run_id
                    token = folder.name.removeprefix("ca-m114-recovery-")
                    blocked = p.retry_recovery(token)
                    self.assertEqual(blocked["recovery_result"], "ACTIVE")
                    handle.close()
            self.assertTrue(folder.is_dir())
            self.assertEqual(folder.stat().st_mode & 0o777, 0o700)
            self.assertEqual(report["recovery_token"], token)
            def wrong_label(args, timeout):
                self.assertNotIn("rm", args)
                return p.subprocess.CompletedProcess(args, 0,
                    "wrong-label" if "inspect" in args else "", "")
            with mock.patch("run_ledger._fixed", side_effect=wrong_label):
                result = p.retry_recovery(token)
                self.assertEqual(result["recovery_result"], "RECOVERY_BLOCKED")
            self.assertTrue(folder.is_dir())
            with p.RunLedger(folder / "private-ledger") as ledger:
                handle_state = ledger.get(rid)["state"]
                self.assertEqual(handle_state, "RECOVERY_BLOCKED")
            replies = iter([p.subprocess.CompletedProcess([], 0, "", ""),
                            p.subprocess.CompletedProcess([], 0, rid+"\n", ""),
                            p.subprocess.CompletedProcess([], 0, "", ""),
                            p.subprocess.CompletedProcess([], 1, "", "")])
            with mock.patch("run_ledger._fixed", side_effect=lambda *a, **k: next(replies)), \
                 mock.patch.object(p, "fixed", return_value=p.subprocess.CompletedProcess([],1,"","")):
                result = p.retry_recovery(token)
            self.assertEqual(result["status"], "REAL_RECOVERY_RETRY_PASS")
            self.assertFalse(folder.exists())

    @unittest.skipUnless(sys.platform == "linux", "Linux private-lock semantics")
    def test_clean_probe_directory_is_removed_but_uncertain_exception_retained(self):
        if os.geteuid() == 0:
            self.skipTest("nonroot host required")
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(p, "ARTIFACTS", pathlib.Path(temp)):
            report = {}
            with p.retained_ledger(report) as folder:
                pass
            self.assertFalse(folder.exists())
            report = {}
            with self.assertRaises(OSError):
                with p.retained_ledger(report) as folder:
                    raise OSError("private details")
            self.assertTrue(folder.is_dir())
            self.assertEqual(report["reason"], "RECOVERY_CLEANUP_BLOCKED")
            self.assertNotIn("private details", str(report))

    def test_probe_record_cannot_target_unrelated_run(self):
        row = {"request_id": p.REQUEST_ID, "plan_hash": p.TEST_PLAN,
               "run_id": "a"*24, "container_name": p.managed_container("a"*24)}
        self.assertTrue(p.probe_record(row))
        for change in [{"request_id":"b"*32},{"plan_hash":"b"*64},
                       {"container_name":p.managed_container("b"*24)}]:
            self.assertFalse(p.probe_record({**row, **change}))


if __name__ == "__main__":
    unittest.main()
