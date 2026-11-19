"""M1.10 scoped disposable writable-worktree contracts; no real Podman runs."""
from __future__ import annotations

import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m0"))
sys.path.insert(0, str(ROOT / "tools/m1"))
spec = importlib.util.spec_from_file_location(
    "scoped_worktree_probe", ROOT / "tools/m1/scoped_worktree_probe.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
M0_ID = "a" * 64

def done(cmd, status=0, out=""):
    return subprocess.CompletedProcess(cmd, status, out, "")

def allow_fixture(source):
    base = pathlib.Path(source)
    (base / "App.cs").write_text(m.APP_AFTER, encoding="utf-8")
    (base / "Feature.cs").write_text(m.FEATURE, encoding="utf-8")
    (base / "Child.cs").write_text(m.CHILD, encoding="utf-8")
    (base / "escape").symlink_to("/etc/passwd")

class ScopedWorktreeContracts(unittest.TestCase):
    def test_fixed_guest_positive_and_negative_workloads(self):
        self.assertEqual(len(m.CASES), 10)
        self.assertEqual(len(set(m.CASES)), 10)
        js = m.WORKLOAD_JS
        for needle in (
            "fs.writeFileSync(app, expectedAfter",
            "fs.writeFileSync('/workspace/Feature.cs'",
            "cp.spawnSync(process.execPath",
            "fs.symlinkSync('/etc/passwd','/workspace/escape')",
            "fs.writeFileSync('/workspace/escape'",
            "'/workspace/../unapproved/keep.txt'",
            "'/workspace/.git'",
            "'/home/dave/CodingAssistant'",
            "fs.writeFileSync('/etc/ca-m110-escape'",
            "child_unapproved_write_denied",
        ):
            self.assertIn(needle, js)
        for forbidden in ("session/new", "session/prompt",
                          "git push", "http://", "https://"):
            self.assertNotIn(forbidden, js)

    def test_only_one_exact_disposable_bind_mount(self):
        with tempfile.TemporaryDirectory() as root:
            with mock.patch.object(m, "ARTIFACTS", pathlib.Path(root)):
                with m.fixture() as target:
                    approved, _ = m.setup(pathlib.Path(target))
                    argv = m.command_for(
                        m.OWNER_IMAGE_ID, "ca-m110-scope-012345abcdef",
                        1000, 1000, approved)
                    for key in (
                        "--pull=never", "--network=none", "--read-only",
                        "--cap-drop=all", "--security-opt=no-new-privileges",
                        "--userns=keep-id", "--user=1000:1000",
                        "--pids-limit=96", "--memory=2g", "--cpus=2",
                        "--workdir=/workspace", "--entrypoint=node",
                    ):
                        self.assertIn(key, argv)
                    self.assertEqual(argv.count("--volume"), 1)
                    volume = argv[argv.index("--volume")+1]
                    self.assertEqual(volume,
                                     str(approved.resolve()) + ":/workspace:rw,nosuid,nodev")
                    self.assertNotIn("/home/dave/CodingAssistant:", volume)
                    for key in ("--network=host", "--publish", "--privileged",
                                "--env-file", "--device"):
                        self.assertNotIn(key, argv)
                    self.assertEqual(argv[argv.index("--entrypoint=node")+1],
                                     m.OWNER_IMAGE_ID)
                    with self.assertRaises(ValueError):
                        m.command_for("f"*64, "ca-m110-scope-012345abcdef",
                                      1000, 1000, approved)
                    with self.assertRaises(ValueError):
                        m.command_for(m.OWNER_IMAGE_ID, "ca-m110-scope-bad",
                                      1000, 1000, approved)
                    with self.assertRaises(ValueError):
                        m.command_for(m.OWNER_IMAGE_ID, "ca-m110-scope-012345abcdef",
                                      0, 1000, approved)
                    (approved / ".git").mkdir()
                    with self.assertRaises(ValueError):
                        m.command_for(m.OWNER_IMAGE_ID, "ca-m110-scope-012345abcdef",
                                      1000, 1000, approved)

    def test_no_arbitrary_host_directory_accepted(self):
        with tempfile.TemporaryDirectory() as fake, tempfile.TemporaryDirectory() as root:
            with mock.patch.object(m, "ARTIFACTS", pathlib.Path(root)):
                approve = pathlib.Path(fake) / "approved"
                approve.mkdir()
                (approve / "App.cs").write_text(m.APP_BEFORE)
                (approve / "Notes.txt").write_text(m.UNCHANGED)
                with self.assertRaises(ValueError):
                    m.command_for(m.OWNER_IMAGE_ID, "ca-m110-scope-012345abcdef",
                                  1000, 1000, approve)

    def test_independent_host_audit_detects_neighbor_tampering(self):
        with tempfile.TemporaryDirectory() as root:
            approved, outside = m.setup(pathlib.Path(root))
            hash_before = m.hash_file(outside)
            allow_fixture(approved)
            self.assertEqual(len(m.verify_host(approved, outside, hash_before)), 9)
            outside.write_text("tampered", encoding="utf-8")
            with self.assertRaises(ValueError):
                m.verify_host(approved, outside, hash_before)

    def test_guest_payload_requires_ten_verified_booleans(self):
        good={key:True for key in m.CASES}
        self.assertEqual(m.parse(json.dumps(good)),good)
        for bad in (
            {**good,m.CASES[1]:False},
            {**good,m.CASES[2]:"true"},
            {**good,"extra":True},
            {k:v for k,v in good.items() if k!=m.CASES[0]},
            "anything", [], {},
        ):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                m.parse(json.dumps(bad))

    @mock.patch.object(m.sys, "platform", "linux")
    @mock.patch.object(m.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getgid", return_value=1000, create=True)
    def test_good_guest_and_independent_sibling_hash(self, *_patches):
        calls=[]
        def fixed(cmd, timeout=50):
            calls.append(cmd)
            if cmd[1]=="run":
                mounted=cmd[cmd.index("--volume")+1].split(":/workspace:",1)[0]
                allow_fixture(mounted)
                return done(cmd,out=json.dumps({k:True for k in m.CASES}))
            if cmd[1]=="rm":
                return done(cmd)
            if cmd[1:3]==["container","exists"]:
                return done(cmd,status=1)
            raise AssertionError(cmd)
        with tempfile.TemporaryDirectory() as temp, \
             mock.patch.object(m,"ARTIFACTS",pathlib.Path(temp)), \
             mock.patch.object(m,"verify_inputs",return_value=M0_ID), \
             mock.patch.object(m,"inspect_composed",return_value=m.OWNER_IMAGE_ID), \
             mock.patch.object(m,"verify_combined",return_value={
                 "registry":{"id":"opencode","installation":"installed",
                             "expectedCommand":True}}), \
             mock.patch.object(m,"fixed",side_effect=fixed):
            result=m.run()
        self.assertEqual(result["status"],"SCOPED_WORKTREE_PASS",result)
        self.assertEqual(len(result["guest_checks"]),10)
        self.assertEqual(len(result["host_audit"]),9)
        self.assertTrue(result["guest_removed"])
        self.assertTrue(result["temporary_fixture_removed"])
        self.assertFalse(result["model_inference"])
        self.assertFalse(result["actual_user_worktree_touched"])
        self.assertEqual(sum(c[1]=="run" for c in calls),1)
        self.assertEqual(sum(c[1]=="rm" for c in calls),1)

    @mock.patch.object(m.sys, "platform", "linux")
    @mock.patch.object(m.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getgid", return_value=1000, create=True)
    def test_any_guest_false_or_cleanup_failure_blocks(self, *_patches):
        for failure in ("guest","cleanup","independent-host"):
            def fixed(cmd,timeout=50):
                if cmd[1]=="run":
                    mounted=cmd[cmd.index("--volume")+1].split(":/workspace:",1)[0]
                    allow_fixture(mounted)
                    if failure=="independent-host":
                        (pathlib.Path(mounted).parent/"unapproved/keep.txt").write_text(
                            "TAMPER",encoding="utf-8")
                    observations={k:True for k in m.CASES}
                    if failure=="guest": observations["approved_new_file_succeeded"]=False
                    return done(cmd,out=json.dumps(observations))
                if cmd[1]=="rm":return done(cmd)
                if cmd[1:3]==["container","exists"]:
                    return done(cmd,status=0 if failure=="cleanup" else 1)
                raise AssertionError(cmd)
            with tempfile.TemporaryDirectory() as temp, \
                 mock.patch.object(m,"ARTIFACTS",pathlib.Path(temp)), \
                 mock.patch.object(m,"verify_inputs",return_value=M0_ID), \
                 mock.patch.object(m,"inspect_composed",return_value=m.OWNER_IMAGE_ID), \
                 mock.patch.object(m,"verify_combined",return_value={
                     "registry":{"id":"opencode","installation":"installed",
                                 "expectedCommand":True}}), \
                 mock.patch.object(m,"fixed",side_effect=fixed):
                result=m.run()
            self.assertEqual(result["status"],"BLOCKED",(failure,result))

    @mock.patch.object(m.sys,"platform","win32")
    def test_windows_is_not_claimed_green(self):
        with mock.patch.object(m,"fixed") as fn:
            result=m.run()
        self.assertEqual(result["status"],"BLOCKED")
        fn.assert_not_called()

if __name__=="__main__":
    unittest.main()
