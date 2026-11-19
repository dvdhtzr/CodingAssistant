# M0.13 — First real local Qwen native OpenCode edit (narrow pilot)

**Status: pending GitHub CI and owner-run Dell evidence. Overall M0 remains YELLOW.**

M0.12 demonstrated that pinned OpenCode v2.3.0 can execute one native
`edit` tool call against a disposable C# fixture with a deterministic model
mock. The M0.11 real-model pilot proposed C# source as text and let a trusted
controller apply one validated correction. **M0.13 is the first trial of a
real Qwen function/tool decision executed by OpenCode's native edit tool.**

## Security boundaries

1. The entry point is `tools/m0/real_opencode_edit_probe.py`, requiring a
   non-root Linux host, rootless Podman and an already verified immutable
   M0.7 image ID. It never builds/pulls images.
2. The trusted runner copies ONLY the frozen `02-build-repair/api` files
   into a fresh disposable folder. Their original checksums are retained.
   No source checkout, credentials, Git metadata, remote service or host
   home is mounted.
3. OpenCode runs as the host's normal unprivileged UID inside Podman,
   `--network=none`, `--read-only`, `--cap-drop=all`, no new privileges,
   and with bounded memory, PID count and runtime.
4. Exactly two mounts exist during OpenCode editing: a **read-only private
   Unix-socket directory** for local model transport and a **writable
   disposable fixture copy** at `/workspace`. The socket broker is not a
   generic network proxy. The container has no public egress.
5. Native OpenCode permissions deny by default and allow edit of only the
   fixed `Program.cs`. The host broker independently reconstructs the
   Qwen prompt and advertises only ONE function named `edit` with
   `path`, `oldString` and `newString`. It does not forward OpenCode's
   broad tool catalog or hidden system prompt.
6. The broker accepts exactly one **real model-generated** function decision
   only when its parsed arguments match exactly:
   `/workspace/Program.cs`, `values.Summ()`, `values.Sum()`. Text
   instead of a tool call, more tools, malformed JSON/SSE, incomplete
   results, different model/digest, path or edit are blocked before they
   can reach OpenCode.
7. After OpenCode executes the real-model-selected edit, one follow-up
   request with the expected tool ID is permitted. The broker sends a
   **fixed final acknowledgement** with NO further model inference. The
   container and trusted host separately verify the actual file bytes,
   unchanged project file, absence of new files and source/sentinel
   integrity. One unanticipated request fails the run.
8. A **second** no-network Podman container, without any inference socket,
   executes the trusted fixed offline .NET restore, build and console
   evaluator. The expected behavior is exactly `Total: 15\n`. The
   original fixture must remain unchanged; both temporary workspaces
   and the Unix socket are removed after the run.

The second response is deliberately controller-generated; **the initial
native tool decision must be made by real local Qwen**, not a mock. This
pilot does not allow arbitrary OpenCode shell commands or general
filesystem edits, and does not verify multi-file autonomous repair.

## CI versus real-model acceptance

CI runs Python model-free negative tests, Node syntax checks, and the
earlier M0.12 deterministic OpenCode tool contract with an independent
offline C# evaluator. **No CI job actually calls Qwen**. Only the Dell
owner-run probe below can produce real Qwen evidence. Do not mistake CI
success for M0.13 acceptance or M0 GREEN.

## Dell command — only after PR verification and merge

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/real_opencode_edit_probe.py --model qwen3.5:4b
```

Expected success banner:

`M0.13 REAL OPENCODE EDIT REAL_EDIT_PASS: qwen3.5:4b`

All 16 native edit checks and 10 independent evaluation checks must pass,
the provider model counts must be one attempt/one completed, total chat
requests two with one follow-up, and host source integrity flags true.
Ignored local evidence:

`artifacts/m0/real-opencode-edit.json`

If blocked, share only the printed checks and reason. Do not change
container network permissions, use sudo, add general shell/edit privileges,
mount the source checkout, or use a model fallback to force a pass.

## Exit criteria still outstanding

M0.13 is a **single frozen repair** and cannot replace the independently
reviewed M0 qualification: three different assignments × three runs per
candidate, >=2/3 success on each, >=7/9 total, zero unauthorized actions,
zero false verified completions, no unrecoverable failures, and independent
security review. Windows isolation and evaluation must be separately
accepted. M1–M3 must not begin until those gates are satisfied.
