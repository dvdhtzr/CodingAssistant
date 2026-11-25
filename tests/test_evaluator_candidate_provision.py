"""Candidate-only evaluator provenance policy tests (all builds mocked)."""
from __future__ import annotations

import hashlib
import pathlib
import subprocess
import sys
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
import evaluator_candidate_provision as runner


class EvaluatorProvisioningTests(unittest.TestCase):
    def test_build_uses_inspected_id_and_only_reviewed_inputs(self):
        base_id, built_id = "a" * 64, "b" * 64
        contexts = []

        def inspect(image, timeout):
            expected = base_id if image == runner.RUNTIME_IMAGE else built_id
            self.assertIn(image, (runner.RUNTIME_IMAGE, runner.EVAL_IMAGE, built_id))
            return {"present": True, "image_id": "sha256:" + expected}

        def build(argv, **kwargs):
            self.assertEqual(argv[:3], ["podman", "build", "--pull=never"])
            self.assertEqual(argv[argv.index("--build-arg") + 1], "BASE_IMAGE=" + base_id)
            context = pathlib.Path(argv[-1])
            contexts.append(context)
            self.assertEqual({p.relative_to(context).as_posix()
                              for p in context.rglob("*") if p.is_file()},
                             set(runner.EVALUATOR_INPUTS))
            self.assertNotEqual(context, runner.ROOT)
            for relative in runner.EVALUATOR_INPUTS:
                self.assertEqual((context / relative).read_bytes(),
                                 (runner.ROOT / relative).read_bytes())
            pathlib.Path(argv[argv.index("--iidfile") + 1]).write_text(
                "sha256:" + built_id + "\n", encoding="ascii")
            return subprocess.CompletedProcess(argv, 0, "", "")

        with (patch.object(runner.sys, "platform", "linux"),
              patch.object(runner.os, "geteuid", return_value=1000, create=True),
              patch.object(runner, "inspect_podman", return_value={"rootless": True}),
              patch.object(runner, "image_status", side_effect=inspect) as inspect_call,
              patch.object(runner.subprocess, "run", side_effect=build)):
            report = runner.provision_evaluator()
        self.assertEqual(report["status"], "CANDIDATE_PROVISIONED_NOT_QUALIFIED")
        self.assertEqual(report["runtime_image_id"], base_id)
        self.assertEqual(report["evaluator_image_id"], built_id)
        self.assertEqual(report["recipe_inputs_sha256"], {
            relative: hashlib.sha256((runner.ROOT / relative).read_bytes()).hexdigest()
            for relative in runner.EVALUATOR_INPUTS})
        self.assertEqual([c.args[0] for c in inspect_call.call_args_list],
                         [runner.RUNTIME_IMAGE, built_id, runner.EVAL_IMAGE])
        self.assertFalse(contexts[0].exists())

    def test_mutable_and_malformed_base_ids_block_before_build(self):
        for bad in (None, "latest", runner.RUNTIME_IMAGE, "a" * 12, "a" * 63,
                    "sha256:" + "z" * 64):
            with self.subTest(bad=bad):
                with (patch.object(runner.sys, "platform", "linux"),
                      patch.object(runner.os, "geteuid", return_value=1000, create=True),
                      patch.object(runner, "inspect_podman", return_value={"rootless": True}),
                      patch.object(runner, "image_status",
                                   return_value={"present": True, "image_id": bad}),
                      patch.object(runner.subprocess, "run") as build):
                    with self.assertRaises(RuntimeError):
                        runner.provision_evaluator()
                    build.assert_not_called()

    def test_missing_or_substituted_build_identity_blocks(self):
        base, built, substituted = "a" * 64, "b" * 64, "c" * 64
        for receipt, exact, tagged in (
            (None, built, built), ("latest", built, built),
            (base, base, base), (built, substituted, built),
            (built, built, substituted), (built, None, built),
        ):
            with self.subTest(receipt=receipt, exact=exact, tagged=tagged):
                def inspect(image, timeout):
                    value = (base if image == runner.RUNTIME_IMAGE else
                             tagged if image == runner.EVAL_IMAGE else exact)
                    return {"present": value is not None, "image_id": value}

                def build(argv, **kwargs):
                    if receipt is not None:
                        pathlib.Path(argv[argv.index("--iidfile") + 1]).write_text(
                            receipt, encoding="ascii")
                    return subprocess.CompletedProcess(argv, 0, "", "")

                with (patch.object(runner.sys, "platform", "linux"),
                      patch.object(runner.os, "geteuid", return_value=1000, create=True),
                      patch.object(runner, "inspect_podman", return_value={"rootless": True}),
                      patch.object(runner, "image_status", side_effect=inspect),
                      patch.object(runner.subprocess, "run", side_effect=build)):
                    with self.assertRaises(RuntimeError):
                        runner.provision_evaluator()


    def test_default_is_nonexecuting_and_candidate_tag_is_separate(self):
        with (patch.object(sys, "argv", ["evaluator_candidate_provision.py"]),
              patch.object(runner, "provision_evaluator") as provision):
            self.assertEqual(runner.main(), 0)
            provision.assert_not_called()
        self.assertNotEqual(runner.EVAL_IMAGE,
                            "localhost/codingassistant-m0-qualification:1")


if __name__ == "__main__":
    unittest.main()
