# M0.10 — Deterministic adversarial workspace containment (NO AGENT)

**Status: PENDING Dell qualification. M0 overall remains YELLOW.**

The owner reported M0.9 WORKSPACE_PRECHECK_PASS. That established fixed isolated workspace/child-process behavior, not safety of a coding agent. M0.10 deliberately attempts prohibited operations while **still using only hard-coded, reviewed programs**—never model-generated code.

## Fixed negative checks

The Python controller verifies a non-root, rootless Podman host, requires the immutable-ID existing offline toolchain image, validates the frozen 02-build-repair source fixture and copies it into a fresh temporary directory. It never mounts the original CodingAssistant repository or host home.

The Node process receives one writable disposable workspace mount and the same restrictions as M0.9: network=none, read-only root, zero added capabilities, no-new-privileges, resource limits, explicit non-root user and private tmpfs with notmpcopyup.

It verifies: non-root identity; zero effective capabilities; new privileges disabled; root mount read-only; host sentinel hidden; root configuration write denied; path-traversal write denied; symlink-outside write denied (actual follow-symlink attempt, not a misleading exclusive-create check); symlink-host read denied; scoped workspace write allowed; unrelated Git checkout absent; private inference socket absent; child process still unprivileged; child external TCP denied; child cannot see host sentinel; outbound Git remote access denied. The host then independently confirms **both** original fixture files are unchanged, the disposable workspace returns to its original content hash, and the host sentinel was not altered.

This is a suite of **18 check statuses** and requires container exit code 0. It is a *negative preflight*, **not** permission to execute generated code. It does not simulate all possible kernel escape classes, enforce content-level file policies on an LLM, prove cancellation/recovery, provide full supply-chain integrity, or meet the independent M1 ten-test security requirement.

## Execution on Dell (after CI and PR merge)

    cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/workspace_negative_probe.py

A successful run reports:

    M0.10 DISPOSABLE SECURITY NEGATIVE_PRECHECK_PASS

Evidence is written only into ignored local artifacts/m0/workspace-negative.json. No model inference, OpenCode agent, generated command, Git push or build/test execution occurs.

**Failure rule:** any false or absent check, unexpected source mutation, altered sentinel, missing image, non-root failure or nonzero container exit means BLOCKED. Do not use sudo, unconfined images, disabled Linux security controls, host-network mounts or your source checkout to obtain a pass.

## What must precede the first AI-written code

The next design/implementation gate is a **separately reviewed, bounded coding execution harness** combining a disposable OS-isolated copy, pin-checked private Ollama broker, tool-call constraints and budgets, mandatory process/child and filesystem restrictions, independent source-state and acceptance tests, graceful cancellation and crash recovery, and evidence collection. Existing tools/m0/benchmark.py explicitly lacks these protections and must **not** run on the Dell host as a substitute. Only after those gates should one frozen benchmark task be admitted for a controlled trial, followed by all 3 x 3 qualification runs and independent safety review.
