# M0 qualification — sealed nine-workspace isolation preflight

**Stage M0.14, not model qualification.** M0.13 proved one real Qwen native
OpenCode edit of a frozen C# file. The next goal is the original three
independent task types × three independent attempts/model, with separately
reviewed safety evidence. We must not launch the old unisolated
`tools/m0/benchmark.py` on the owner's working machine.

## What this slice actually proves

`tools/m0/qualification_isolation_probe.py` verifies that **each of the
three original, unchanged benchmarks** can be copied fresh and
independently placed into the existing immutable local M0.7 runtime image.
Each of the nine trials:

1. Validates the original `benchmarks/tasks.json` IDs and a **hardcoded
   frozen exact file list** for its task; rejects extra files, missing
   files, symlinks, hard links and oversized inputs. Records SHA-256 of
   the unchanged source tree.
2. Copies that task into a new disposable folder. Executes **one
   non-agent Node verifier** inside rootless Podman with a **single
   writable mount of that disposable copy**. No Git checkout, home,
   real source, credentials, or model socket is mounted.
3. Requires `--network=none`, `--read-only`, non-root identity,
   `--cap-drop=all`, `--security-opt=no-new-privileges`,
   `--pull=never`, bounded PID count/memory, and an empty tmpfs.
   Includes negative external-TCP, unmounted host/sentinel, and
   fixed child process privilege/TCP checks. Confirms .NET 10,
   Node 22, npm and pinned OpenCode 2.3.0 binaries are accessible.
4. Creates and immediately removes **one fixed probe marker** in the
   disposable copy. It never edits source.
5. Independently rehashes original and disposable files after
   process exit; refuses any drift. Records task, repeat, isolation
   check results, immutable image ID and input/output hashes.
6. Writes local ignored evidence after each run and stops immediately
   on any failed guard. Only all nine passing yields
   `ISOLATION_MATRIX_PASS`.

GitHub CI independently exercises the same guest/OS restrictions using
Docker **for each of the three task types**. Dell must pass all
nine using its own rootless Podman setup. The actual nine-run coding
benchmark, real Qwen prompts, generated code, independent test results,
model safety reviews and complete product security assessment are
**not** included in this preflight.

## Run on Dell after CI GREEN and merge

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/qualification_isolation_probe.py
```

Expected terminal completion is:

`M0.14 FROZEN QUALIFICATION ISOLATION ISOLATION_MATRIX_PASS`

`Completed: 9 / 9`

The ignored evidence file is
`artifacts/m0/qualification-isolation.json`. If BLOCKED, share
the output/failed check. **Never use sudo, host networking, a
workstation-level unisolated benchmark, or broader filesystem mounts
as a workaround.**

## Next implementation: real qualifications, not more mock pilots

This preflight is a *prerequisite*, not an acceptance substitute.
The actual qualification runner must:

- use the frozen prompts and untouched fixture copies for 3 × 3
  **real model-driven** attempts;
- enforce the proved OS boundary for the **whole OpenCode/tool/process
  tree**, not just the launcher;
- keep inference on one pinned Ollama model/digest through a bounded,
  exact-route host broker; do not tunnel host/network commands;
- allow only explicit workspace-scoped read/edit/write/tool behavior,
  reject privilege, Git remote and network escape attempts, and
  retain bounded trace evidence for an independent safety reviewer;
- give each attempt a fresh initial snapshot and preserve start/end
  hashes and per-run outputs; run independent acceptance in a separate
  network-disabled container with no model socket;
- provision React/TypeScript/Vitest dependencies offline in the
  evaluator, with a reproducible lock/cache (currently missing);
- record `verified` only after actual .NET/API/React behavior is
  checked, preserve safety fields as **null/unknown** until review;
- apply the original qualification gate: ≥2/3 each task, ≥7/9
  overall, zero safety/recovery failures, completed independent review.

**Overall M0 YELLOW** until that complete process passes.
