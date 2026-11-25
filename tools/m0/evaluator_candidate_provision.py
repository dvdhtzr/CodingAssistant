#!/usr/bin/env python3
"""Explicit candidate-only evaluator provisioning; no model or source execution.

Historical M0.16 runner and M0.18 audit are frozen. This separate command
does not replace their evaluator tag or authorize any candidate for execution.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
from typing import Any

from common import ROOT
from runtime_image_probe import IMAGE as RUNTIME_IMAGE
from sandbox_probe import image_status, inspect_podman

EVAL_IMAGE = "localhost/codingassistant-m0-qualification-candidate:1"

EVALUATOR_INPUTS = (
    "infra/qualification/Containerfile.candidate",
    "benchmarks/fixtures/03-fullstack-feature/web/package.json",
    "benchmarks/acceptance/TaskSummary.acceptance.test.tsx",
)


def immutable_image_id(value: object) -> str:
    """Normalize an inspected full local content ID; never accept a tag."""
    if not isinstance(value, str):
        raise RuntimeError("Full immutable image ID required")
    match = re.fullmatch(r"(?:sha256:)?([0-9a-f]{64})", value)
    if match is None:
        raise RuntimeError("Full immutable image ID required")
    return match.group(1)


def stage_evaluator_inputs(context: pathlib.Path) -> dict[str, str]:
    """Copy only fixed reviewed recipe inputs, not the whole owner repo."""
    hashes = {}
    for relative in EVALUATOR_INPUTS:
        source = ROOT / relative
        if (not source.is_file() or source.is_symlink() or
                any(parent.is_symlink() for parent in
                    source.parents if parent != ROOT and ROOT in parent.parents)):
            raise RuntimeError("Trusted qualification input absent or linked")
        data = source.read_bytes()
        target = context / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        hashes[relative] = hashlib.sha256(data).hexdigest()
    return hashes


def provision_evaluator() -> dict[str, Any]:
    """EXPLICIT trusted image build; never invoked by the model runner."""
    if sys.platform != "linux" or os.geteuid() == 0:
        raise RuntimeError("Build evaluator as normal Linux user only")
    if inspect_podman(20).get("rootless") is not True:
        raise RuntimeError("Rootless Podman unavailable")
    base = image_status(RUNTIME_IMAGE, 20)
    if not base.get("present"):
        raise RuntimeError("M0.7 runtime image not present")
    base_id = immutable_image_id(base.get("image_id"))
    # Dependencies may download only at this owner-requested provisioning
    # step. Runtime evaluations and all model calls are offline/local.
    with tempfile.TemporaryDirectory(prefix="ca-evaluator-build-") as temp:
        parent = pathlib.Path(temp)
        context = parent / "context"
        context.mkdir()
        hashes = stage_evaluator_inputs(context)
        iidfile = parent / "image-id"
        command = ["podman", "build", "--pull=never",
                   "--build-arg", "BASE_IMAGE=" + base_id,
                   "--iidfile", str(iidfile),
                   "--file", str(context / EVALUATOR_INPUTS[0]),
                   "--tag", EVAL_IMAGE, str(context)]
        completed = subprocess.run(
            command, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            encoding="utf-8", errors="replace", timeout=1200, check=False)
        if completed.returncode != 0:
            raise RuntimeError("Qualification image build failed: " +
                               completed.stderr[-400:])
        if not iidfile.is_file() or iidfile.is_symlink():
            raise RuntimeError("Build did not record an immutable image ID")
        built_id = immutable_image_id(iidfile.read_text(encoding="ascii").strip())
        if built_id == base_id:
            raise RuntimeError("Evaluator image unexpectedly aliases its runtime base")
        result = image_status(built_id, 20)
        if (not result.get("present") or
                immutable_image_id(result.get("image_id")) != built_id):
            raise RuntimeError("Built image identity could not be verified")
        tagged = image_status(EVAL_IMAGE, 20)
        if (not tagged.get("present") or
                immutable_image_id(tagged.get("image_id")) != built_id):
            raise RuntimeError("Evaluator tag no longer identifies the exact build")
    # This is provisioning evidence, not independent security qualification.
    return {"status": "CANDIDATE_PROVISIONED_NOT_QUALIFIED", "runtime_image_id": base_id,
            "evaluator_image_id": built_id, "recipe_inputs_sha256": hashes}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare", action="store_true",
                        help="Explicitly provision a separate unqualified candidate")
    args = parser.parse_args()
    if not args.prepare:
        print("NOT_ATTEMPTED: use --prepare for trusted candidate provisioning")
        return 0
    try:
        report = provision_evaluator()
    except (RuntimeError, ValueError, OSError, subprocess.TimeoutExpired) as exc:
        print("BLOCKED:", type(exc).__name__, str(exc)[:300], file=sys.stderr)
        return 2
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
