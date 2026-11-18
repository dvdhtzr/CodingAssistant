# M0.9 — Disposable coding-workspace preflight (NO agent)

**Status: PENDING Dell evidence. Overall M0 remains YELLOW.**

## Why

M0.8b-2b returned REAL_INFERENCE_PASS on Dell (November 18, 2026): actual pinned OpenCode communicated with real Ollama Qwen 4B through the private rootless AF_UNIX bridge, with exactly one completed inference and all fourteen checks PASS. **No coding tools, source checkout or generated code executed.**

The approved M0 exit criteria require three coding tasks run three times each from identical isolated starts. The existing tools/m0/benchmark.py does **not** provide OS enforced isolation and its unisolated-risk acknowledgement is not a security boundary. Do not run that benchmark directly on the Dell or Windows PC.

## Fixed preflight

The new tools/m0/disposable_workspace_probe.py:

1. Requires non-root Linux Podman and the existing immutable-ID M0.7 toolchain image. It never pulls or builds images.
2. Validates the frozen fixture at benchmarks/fixtures/02-build-repair/api contains only Program.cs and Repair.csproj as regular, nonlinked files; rejects unexpected entries and oversized input, then hashes both files.
3. Copies only those two files into a fresh auto-cleaned temporary workspace. Creates a host-only sentinel outside the mounted copy.
4. Starts rootless Podman with network=none, read-only root, all capabilities dropped, no-new-privileges, process and memory limits, and a private /tmp tmpfs with notmpcopyup. **Only the disposable fixture copy** is mounted read-write at /workspace. There are no host home, source repository, credentials, Git checkout or socket mounts.
5. Executes a fixed, reviewed Node probe that verifies non-root identity, read-only root mount, zero effective capabilities, no-new-privileges, hidden sentinel, denied external TCP, readable copied fixture, and write/remove capability within the disposable workspace. One **fixed child process** separately verifies identity, root-read-only, no caps, no-new-privileges, host sentinel and network denial.
6. After the container exits, independently verifies that the original two files and the disposable copy retain the exact initial hashes. It deletes its temporary workspace and leaves only bounded pass/fail evidence at artifacts/m0/disposable-workspace.json (Git ignored).

**No OpenCode process, model inference, generated code, npm/dotnet build, Git remote or benchmark is executed.** This is not a full coding sandbox qualification.

## Operator command — only after CI and PR merge

    cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/disposable_workspace_probe.py

Expected result if all sixteen checks pass: M0.9 DISPOSABLE WORKSPACE WORKSPACE_PRECHECK_PASS, container exit code 0.

If BLOCKED, report printed checks and reason. Do not add sudo, public networking, extra mounts, privilege, automatic image download or mutable image tags to obtain a pass.

## Remaining gates

Before generated-code execution, independently test workspace escape via symlink and traversal, subprocess and process-tree restrictions, outbound network/Git remotes, resource limits and timeouts, cancellation, crash recovery, artifact verification and isolated independent acceptance for each fixture. The actual agent must only see the disposable workspace, with OS controls applying to all child processes. The ten M1 negative tests remain mandatory for any production autonomous operation.

Never substitute this static preflight for independent security acceptance or for the approved three-tasks-times-three-repeats benchmark protocol.
