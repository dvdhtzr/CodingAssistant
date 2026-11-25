"""M1 durable host journal: no Podman, Ollama, Gateway or model in CI."""
import importlib.util
import hashlib
import json
import os
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m1"))
import run_ledger as r


def completed(code=0, out=""):
    return subprocess.CompletedProcess([], code, out, "")


class DurableRunLedgerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name) / "private-ledger"
        self.store = r.RunLedger(self.root)
        self.handles = []

    def tearDown(self):
        for handle in self.handles:
            handle.close()
        self.store.close()
        self.temp.cleanup()

    def start(self, request=None):
        handle = self.store.start(request)
        self.handles.append(handle)
        return handle

    def approved(self, handle):
        handle.advance("PREFLIGHT")
        handle.advance("AWAITING_APPROVAL", plan_hash="a" * 64)
        handle.advance("APPROVED")

    def running(self, handle):
        self.approved(handle)
        handle.advance("RUNNING", container_name=handle.container_name())

    def test_durable_database_and_locks_owner_private(self):
        handle = self.start()
        self.approved(handle)
        path = self.root / "runs.sqlite3"
        self.assertTrue(path.is_file())
        self.assertEqual(self.store.transitions(handle.run_id), [
            "NEW", "PREFLIGHT", "AWAITING_APPROVAL", "APPROVED"])
        self.assertEqual(self.store.get(handle.run_id)["plan_hash"], "a" * 64)
        self.assertEqual(len(self.store.history()), 1)
        if os.name == "posix":
            self.assertEqual(path.stat().st_mode & 0o077, 0)
            self.assertEqual(self.root.stat().st_mode & 0o077, 0)
            self.assertEqual((self.root / "run-locks" /
                              (handle.run_id + ".lock")).stat().st_mode & 0o077, 0)
        # Reopen via independent connection while the original process is live.
        with r.RunLedger(self.root) as later:
            self.assertEqual(later.get(handle.run_id)["state"], "APPROVED")
            self.assertEqual(later.reconcile(handle.run_id)["result"], "ACTIVE")

    def test_missing_approval_cannot_run_and_decline_is_terminal(self):
        handle = self.start()
        handle.advance("PREFLIGHT")
        with self.assertRaises(ValueError):
            handle.advance("RUNNING", container_name=handle.container_name())
        handle.advance("AWAITING_APPROVAL", plan_hash="b" * 64)
        handle.advance("DECLINED", result_code="NOT_APPROVED")
        self.assertEqual(handle.finish({"status": "NOT_APPROVED"}, "m112-approved-coding-" +
                                       "0" * 12 + ".json"), "DECLINED")
        handle.close()
        self.assertEqual(self.store.reconcile(handle.run_id)["result"],
                         "ALREADY_TERMINAL")

    def save_evidence(self, handle, name, report):
        value = {**report, "run_id": handle.run_id,
                 "plan_hash": self.store.get(handle.run_id)["plan_hash"]}
        path = self.root / name
        path.write_text(json.dumps(value), encoding="utf-8")
        path.chmod(0o600)
        return value

    def verified_report(self):
        return {"status": "GATEWAY_APPROVED_CODING_PASS", "phase": "complete",
                "approved": True, "gateway": {"removed": True},
                "evaluator": {"status": "VERIFIED", "verified": True},
                "fixture_unchanged": True, "sentinel_unchanged": True}

    def test_success_requires_real_verify_phase_and_persisted_result(self):
        handle = self.start()
        self.running(handle)
        name = "m112-approved-coding-" + "0" * 12 + ".json"
        report = self.save_evidence(handle, name, self.verified_report())
        with self.assertRaises(ValueError):
            handle.finish(report, name)
        handle.advance("VERIFYING")
        self.assertEqual(handle.finish(report, name), "SUCCEEDED")
        row = self.store.get(handle.run_id)
        self.assertEqual(row["evidence_name"], name)
        self.assertEqual(row["evidence_sha256"],
                         hashlib.sha256((self.root / name).read_bytes()).hexdigest())
        with self.assertRaises(ValueError):
            handle.advance("RUNNING", container_name=handle.container_name())
        handle.close()
        self.assertEqual(self.store.reconcile(handle.run_id)["result"],
                         "ALREADY_TERMINAL")

    def test_request_key_exactly_once_even_after_failure(self):
        token = "a" * 32
        handle = self.start(token)
        self.approved(handle)
        handle.advance("FAILED", result_code="BLOCKED")
        handle.close()
        with self.assertRaises(r.DuplicateRequest) as ctx:
            self.store.start(token)
        self.assertEqual(ctx.exception.record["run_id"], handle.run_id)
        self.assertEqual(ctx.exception.record["state"], "FAILED")
        self.assertEqual(len(self.store.history()), 1)
        for invalid in ("", "xyz", "0" * 31, "A" * 32, "../../secret"):
            with self.subTest(token=invalid), self.assertRaises(ValueError):
                self.store.start(invalid)

    def test_orphan_cleanup_absent_container_never_reexecutes(self):
        handle = self.start()
        self.running(handle)
        run_id = handle.run_id
        handle.close()
        commands = []
        def runner(command, timeout):
            commands.append(command)
            return completed(code=1)
        result = self.store.reconcile(run_id, runner=runner)
        self.assertEqual(result["result"], "INTERRUPTED_CLEAN")
        self.assertEqual(self.store.get(run_id)["result_code"], "RECOVERED_ORPHAN")
        self.assertEqual(self.store.transitions(run_id)[-2:],
                         ["RECOVERY_BLOCKED", "INTERRUPTED_CLEAN"])
        self.assertEqual(len(commands), 1)
        self.assertEqual(commands[0][:3],
                         ["podman", "container", "exists"])
        self.assertNotIn("run", commands[0])
        self.assertEqual(self.store.reconcile(run_id)["result"], "ALREADY_TERMINAL")

    def test_orphan_reconciles_only_exact_label_verified_container(self):
        handle = self.start()
        self.running(handle)
        run_id = handle.run_id
        name = handle.container_name()
        handle.close()
        commands = []
        responses = iter([
            completed(0), completed(0, run_id + "\n"),
            completed(0), completed(1),
        ])
        def runner(command, timeout):
            commands.append(command)
            return next(responses)
        result = self.store.reconcile(run_id, runner=runner)
        self.assertEqual(result["result"], "INTERRUPTED_CLEAN")
        self.assertEqual([c[:3] for c in commands], [
            ["podman", "container", "exists"],
            ["podman", "inspect", "--format"],
            ["podman", "rm", "-f"],
            ["podman", "container", "exists"],
        ])
        self.assertEqual(commands[2][3], name)
        self.assertIn(r.LABEL, commands[1][3])
        self.assertEqual(len(commands), 4)

    def test_wrong_label_blocks_without_removing_foreign_container(self):
        handle = self.start()
        self.running(handle)
        run_id = handle.run_id
        handle.close()
        commands = []
        def runner(command, timeout):
            commands.append(command)
            return completed(0, "some-other-run\n")
        blocked = self.store.reconcile(run_id, runner=runner)
        self.assertEqual(blocked["result"], "RECOVERY_BLOCKED")
        self.assertEqual(self.store.get(run_id)["state"], "RECOVERY_BLOCKED")
        self.assertEqual(len(commands), 2)
        self.assertFalse(any(c[1:3] == ["rm", "-f"] for c in commands))
        # A later retry may clean the exact verified container.
        responses = iter([
            completed(0), completed(0, run_id), completed(0), completed(1),
        ])
        repaired = self.store.reconcile(
            run_id, runner=lambda command, timeout: next(responses))
        self.assertEqual(repaired["result"], "INTERRUPTED_CLEAN")

    def test_podman_failure_and_timeout_fail_closed(self):
        for failure in ("timeout", "unknown"):
            with self.subTest(failure=failure):
                handle = self.start()
                self.running(handle)
                handle.close()
                def runner(command, timeout):
                    if failure == "timeout":
                        raise subprocess.TimeoutExpired(command, timeout)
                    return completed(9, "PRIVATE ERROR STRING")
                result = self.store.reconcile(handle.run_id, runner=runner)
                self.assertEqual(result["result"], "RECOVERY_BLOCKED")
                self.assertNotIn("PRIVATE", json.dumps(result))
                self.assertEqual(self.store.get(handle.run_id)["state"],
                                 "RECOVERY_BLOCKED")

    def test_crash_at_each_preexecution_phase_never_triggers_podman(self):
        for stop in ("NEW", "PREFLIGHT", "AWAITING_APPROVAL", "APPROVED"):
            with self.subTest(stop=stop):
                h = self.start()
                if stop != "NEW":
                    h.advance("PREFLIGHT")
                if stop in ("AWAITING_APPROVAL", "APPROVED"):
                    h.advance("AWAITING_APPROVAL", plan_hash="c" * 64)
                if stop == "APPROVED":
                    h.advance("APPROVED")
                h.close()
                runner = mock.Mock(side_effect=AssertionError("Must not use Podman"))
                result = self.store.reconcile(h.run_id, runner=runner)
                self.assertEqual(result["result"], "INTERRUPTED_CLEAN")
                runner.assert_not_called()

    def test_invalid_ids_and_filenames_fail_before_any_state_change(self):
        h = self.start()
        h.advance("PREFLIGHT")
        for text in ("/etc/shadow", "X"*24, "0"*23):
            with self.subTest(value=text), self.assertRaises(ValueError):
                self.store.get(text)
        with self.assertRaises(ValueError):
            h.advance("AWAITING_APPROVAL", plan_hash="bad")
        self.assertEqual(self.store.get(h.run_id)["state"], "PREFLIGHT")
        h.advance("AWAITING_APPROVAL", plan_hash="d" * 64)
        h.advance("APPROVED")
        with self.assertRaises(ValueError):
            h.advance("RUNNING", container_name="other-users-container")
        self.assertEqual(self.store.get(h.run_id)["state"], "APPROVED")
        with self.assertRaises(ValueError):
            h.advance("RUNNING", container_name=h.container_name(),
                      evidence_name="../secret")
        self.assertEqual(self.store.get(h.run_id)["state"], "APPROVED")

    def test_unapproved_symlinked_ledger_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = pathlib.Path(directory)
            (parent / "target").mkdir()
            try:
                (parent / "link").symlink_to(parent / "target", target_is_directory=True)
            except (OSError, NotImplementedError):
                self.skipTest("Symlink privileges unavailable")
            with self.assertRaises(ValueError):
                r.RunLedger(parent / "link")

    def test_cleanup_unverified_is_recoverable_not_falsely_failed(self):
        handle = self.start()
        self.running(handle)
        name = "m112-approved-coding-" + "1"*12 + ".json"
        report = self.save_evidence(handle, name, {"status":"BLOCKED", "gateway":{"removed":False}})
        state = handle.finish(report, name)
        self.assertEqual(state,"RECOVERY_BLOCKED")
        handle.close()
        self.assertEqual(self.store.reconcile(
            handle.run_id, runner=lambda cmd, timeout: completed(1))["result"],
            "INTERRUPTED_CLEAN")

    def test_no_raw_private_data_in_sqlite_metadata(self):
        handle = self.start("e" * 32)
        self.approved(handle)
        handle.advance("FAILED", result_code="BLOCKED")
        data = (self.root / "runs.sqlite3").read_bytes()
        self.assertNotIn(b"source code", data)
        self.assertNotIn(b"OPENAI_API_KEY", data)
        self.assertEqual(self.store.get(handle.run_id)["result_code"], "BLOCKED")

    def test_transition_and_event_rollback_together_on_sqlite_write_failure(self):
        h = self.start("1"*32)
        self.running(h)
        self.store.db.execute("""
            CREATE TRIGGER reject_verify BEFORE INSERT ON transitions
            WHEN NEW.state='VERIFYING' BEGIN SELECT RAISE(ABORT, 'io fixture'); END
        """)
        self.store.db.commit()
        with self.assertRaises(sqlite3.DatabaseError):
            h.advance("VERIFYING")
        with r.RunLedger(self.root) as independent:
            self.assertEqual(independent.get(h.run_id)["state"], "RUNNING")
            self.assertNotIn("VERIFYING", independent.transitions(h.run_id))
        h.close()
        result = self.store.reconcile(h.run_id, runner=lambda *_args, **_kw: completed(1))
        self.assertEqual(result["result"], "INTERRUPTED_CLEAN")
        self.assertNotIn("SUCCEEDED", self.store.transitions(h.run_id))

    def test_two_process_duplicate_admission_has_one_durable_winner(self):
        # Two independent SQLite connections, both ready before the gate.
        child = """
import sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
from run_ledger import RunLedger, DuplicateRequest
with RunLedger(Path(sys.argv[2])) as ledger:
    print('READY', flush=True)
    sys.stdin.readline()
    try:
        handle = ledger.start('2'*32)
        print('WIN '+handle.run_id, flush=True)
        handle.close()
    except DuplicateRequest as e:
        print('DUP '+e.record['run_id'], flush=True)
"""
        processes = []
        try:
            for _ in range(2):
                p = subprocess.Popen([sys.executable, "-c", child,
                    str(ROOT / "tools/m1"), str(self.root)],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE, text=True)
                processes.append(p)
            for p in processes:
                self.assertEqual(p.stdout.readline().strip(), "READY")
            for p in processes:
                p.stdin.write("go\n")
                p.stdin.flush()
            results = []
            for p in processes:
                out, err = p.communicate(timeout=30)
                self.assertEqual(p.returncode, 0, err)
                results.append(out.strip().split())
            self.assertEqual(sorted(x[0] for x in results), ["DUP", "WIN"])
            self.assertEqual(results[0][1], results[1][1])
            self.assertEqual(len(self.store.history()), 1)
            self.assertEqual(self.store.transitions(results[0][1]), ["NEW"])
        finally:
            for p in processes:
                if p.poll() is None:
                    p.kill()
                p.communicate(timeout=10)

    def test_closed_handle_and_foreign_valid_container_cannot_mutate_state(self):
        h = self.start()
        self.approved(h)
        foreign = r.managed_container("f"*24)
        if foreign == h.container_name():
            foreign = r.managed_container("e"*24)
        with self.assertRaises(ValueError):
            h.advance("RUNNING", container_name=foreign)
        h.close()
        with self.assertRaises(ValueError):
            h.advance("RUNNING", container_name=h.container_name())
        self.assertEqual(self.store.get(h.run_id)["state"], "APPROVED")

    def test_hardlinked_ledger_file_and_linked_sqlite_sidecar_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            private = pathlib.Path(temp) / "private"
            private.mkdir(mode=0o700)
            db = private / "runs.sqlite3"
            db.write_bytes(b"not a database")
            db.chmod(0o600)
            try:
                os.link(db, private / "linked-copy")
            except (OSError, NotImplementedError):
                self.skipTest("Hard links unavailable")
            with self.assertRaises(ValueError):
                r.RunLedger(private)
            (private / "linked-copy").unlink()
            db.unlink()
            secret = private / "unrelated"
            secret.write_bytes(b"do not open")
            secret.chmod(0o600)
            os.link(secret, private / "runs.sqlite3-journal")
            with self.assertRaises(ValueError):
                r.RunLedger(private)
            self.assertEqual(secret.read_bytes(), b"do not open")

    def test_missing_or_forged_evidence_and_direct_success_are_rejected(self):
        h = self.start()
        self.running(h)
        h.advance("VERIFYING")
        name = "m112-approved-coding-" + "3"*12 + ".json"
        with self.assertRaises(OSError):
            h.finish(self.verified_report(), name)
        with self.assertRaises(ValueError):
            h.advance("SUCCEEDED", result_code="GATEWAY_APPROVED_CODING_PASS")
        bad = self.save_evidence(h, name, {**self.verified_report(), "gateway": {"removed": False}})
        with self.assertRaises(ValueError):
            h.finish(bad, name)
        valid = self.save_evidence(h, name, self.verified_report())
        with self.assertRaises(ValueError):
            h.finish({**valid, "run_id": "f"*24}, name)
        self.assertEqual(self.store.get(h.run_id)["state"], "VERIFYING")
        self.assertIsNone(self.store.get(h.run_id)["evidence_sha256"])

    def test_evidence_written_but_terminal_commit_failed_cannot_be_replayed(self):
        h = self.start("3"*32)
        self.running(h)
        h.advance("VERIFYING")
        name = "m112-approved-coding-" + "4"*12 + ".json"
        report = self.save_evidence(h, name, self.verified_report())
        self.store.db.execute("""
            CREATE TRIGGER reject_success BEFORE INSERT ON transitions
            WHEN NEW.state='SUCCEEDED' BEGIN SELECT RAISE(ABORT, 'commit fixture'); END
        """)
        self.store.db.commit()
        with self.assertRaises(sqlite3.DatabaseError):
            h.finish(report, name)
        h.close()
        with r.RunLedger(self.root) as independent:
            self.assertEqual(independent.get(h.run_id)["state"], "VERIFYING")
            self.assertIsNone(independent.get(h.run_id)["evidence_sha256"])
            self.assertTrue((self.root / name).is_file())
            self.assertEqual(independent.reconcile(h.run_id, runner=lambda *a, **k: completed(1))["result"], "INTERRUPTED_CLEAN")
            with self.assertRaises(r.DuplicateRequest):
                independent.start("3"*32)
            self.assertNotIn("SUCCEEDED", independent.transitions(h.run_id))

    def test_old_schema_migrates_without_inventing_evidence_digest(self):
        self.store.close()
        db = sqlite3.connect(self.root / "runs.sqlite3")
        db.execute("ALTER TABLE runs DROP COLUMN evidence_sha256")
        db.execute("INSERT INTO runs(run_id,state,created_utc,updated_utc) VALUES(?,?,?,?)",
                   ("a"*24, "FAILED", "old", "old"))
        db.commit()
        db.close()
        self.store = r.RunLedger(self.root)
        self.assertIsNone(self.store.get("a"*24)["evidence_sha256"])
        self.assertEqual(self.store.get("a"*24)["state"], "FAILED")


if __name__ == "__main__":
    unittest.main()
