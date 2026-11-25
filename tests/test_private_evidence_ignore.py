"""Git ignore contracts for private local evidence; never inspect real owner data."""
import pathlib
import subprocess
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


class PrivateEvidenceIgnoreTests(unittest.TestCase):
    def test_private_state_and_receipts_are_ignored(self):
        samples = [
            "artifacts/m1/m112-approved-coding-0123456789ab.json",
            "local-copy/codingassistant.db", "local-copy/codingassistant.db-wal",
            "local-copy/codingassistant.db-shm", "local-copy/runs.sqlite3",
            "local-copy/runs.sqlite3-journal", "local-copy/owner-token.dpapi",
            "local-copy/owner-token-123.tmp", "local-copy/private-ledger/evidence.json",
            "local-copy/private-task-ledger/evidence.json",
            "local-copy/ca-m114-recovery-0123456789ab/private-ledger/runs.sqlite3",
            "local-copy/.env", "local-copy/trace.log",
        ]
        for path in samples:
            with self.subTest(path=path):
                check = subprocess.run(["git", "check-ignore", "--no-index", "-q", "--", path],
                                       cwd=ROOT, capture_output=True, timeout=10)
                self.assertEqual(check.returncode, 0, "private path must be ignored")
        tracked = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT,
                                 capture_output=True, timeout=10, check=True)
        names = tracked.stdout.decode("utf-8").split("\x00")
        forbidden = {"codingassistant.db", "codingassistant.db-wal",
                     "codingassistant.db-shm", "runs.sqlite3", "owner-token.dpapi"}
        self.assertFalse([name for name in names if pathlib.PurePosixPath(name).name in forbidden])
