"""Model-free tests of host-scoped review comparison, not runtime qualification."""
import copy
import json
import pathlib
import subprocess
import sys
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m1"))
import host_provenance_review as review


def example():
    return {
        "schema_version": 1, "profile": "dell-linux",
        "host_binding_sha256": "a" * 64,
        "versions": dict(review.VERSIONS),
        "official_repo_digest": review.OFFICIAL,
        "image_ids": {role: str(i + 1) * 64 for i, role in enumerate(review.ROLES)},
        "recipe_inputs_sha256": {p: review.sha((ROOT / p).read_bytes())
                                 for p in review.INPUTS},
    }


def encoded(item):
    return json.dumps(item).encode()


def check(item, profile="dell-linux"):
    data = encoded(item)
    return review.parse_manifest(data, review.sha(data), profile)


class ReviewTests(unittest.TestCase):
    def test_strict_review_manifest(self):
        valid = example()
        self.assertEqual(check(valid), valid)
        with self.assertRaises(ValueError):
            check(valid, "windows-wsl2")
        with self.assertRaises(ValueError):
            review.parse_manifest(encoded(valid), "0" * 64, "dell-linux")
        duplicate = b'{"profile":"dell-linux","profile":"windows-wsl2"}'
        with self.assertRaises(ValueError):
            review.parse_manifest(duplicate, review.sha(duplicate), "dell-linux")
        for mutate in (
            lambda m: m.update(schema_version=True),
            lambda m: m.update(execution_authorized=True),
            lambda m: m["image_ids"].update(runtime="localhost/runtime:latest"),
            lambda m: m["image_ids"].update(runtime=m["image_ids"]["official"]),
            lambda m: m["image_ids"].pop("evaluator"),
            lambda m: m["versions"].update(acpx="latest"),
            lambda m: m.update(official_repo_digest="ghcr.io/openclaw/openclaw:latest"),
            lambda m: m["recipe_inputs_sha256"].update({"../secret": "a" * 64}),
        ):
            bad = copy.deepcopy(valid)
            mutate(bad)
            with self.assertRaises(ValueError):
                check(bad)

    def test_exact_inspections_only_and_never_qualifies(self):
        item = example()
        data = encoded(item)
        calls = []

        def fixed(argv):
            calls.append(argv)
            if argv[1] == "info":
                return subprocess.CompletedProcess(argv, 0, "true", "")
            image_id = argv[-1]
            return subprocess.CompletedProcess(argv, 0, json.dumps([{
                "Id": "sha256:" + image_id,
                "RepoDigests": [review.OFFICIAL],
            }]), "")

        with (patch.object(review, "host_binding", return_value="a" * 64),
              patch.object(review, "fixed", side_effect=fixed)):
            report = review.verify(data, review.sha(data), "dell-linux")
        self.assertEqual(report["status"], "REVIEWED_INPUTS_MATCH_NOT_QUALIFIED")
        self.assertIs(report["execution_authorized"], False)
        self.assertIs(report["runtime_qualified"], False)
        self.assertEqual(calls, [
            ["podman", "info", "--format", "{{.Host.Security.Rootless}}"],
            *[["podman", "image", "inspect", item["image_ids"][role]]
              for role in review.ROLES]])
        self.assertNotIn("host_binding_sha256", report)

    def test_host_or_recipe_mismatch_stops_before_podman(self):
        for host, drift in (("b" * 64, False), ("a" * 64, True)):
            item = example()
            if drift:
                item["recipe_inputs_sha256"][review.INPUTS[0]] = "0" * 64
            data = encoded(item)
            with (patch.object(review, "host_binding", return_value=host),
                  patch.object(review, "fixed") as command):
                with self.assertRaises(ValueError):
                    review.verify(data, review.sha(data), "dell-linux")
                command.assert_not_called()

    def test_wrong_identity_or_official_digest_rejected(self):
        image = "1" * 64
        for metadata in (
            {"Id": "2" * 64, "RepoDigests": [review.OFFICIAL]},
            {"Id": image, "RepoDigests": []},
            {"Id": image, "RepoDigests": [review.OFFICIAL, None]},
            {"Id": image, "RepoDigests": ["ghcr.io/openclaw/openclaw:latest"]},
        ):
            with patch.object(review, "fixed", return_value=subprocess.CompletedProcess(
                    [], 0, json.dumps([metadata]), "")):
                with self.assertRaises(ValueError):
                    review.inspect_id(image, "official")
        with patch.object(review, "fixed", return_value=subprocess.CompletedProcess(
                [], 1, "", "")):
            with self.assertRaises(ValueError):
                review.inspect_id(image, "runtime")

    def test_host_profile_kernel_is_not_interchangeable(self):
        with (patch.object(review.sys, "platform", "linux"),
              patch.object(review.os, "geteuid", return_value=1000, create=True),
              patch.object(review.platform, "release", return_value="6.6-microsoft-standard-WSL2"),
              patch.object(review, "read_regular", return_value=b"1" * 32)):
            with self.assertRaises(ValueError):
                review.host_binding("dell-linux")
            self.assertTrue(review.is_hash(review.host_binding("windows-wsl2")))


if __name__ == "__main__":
    unittest.main()
