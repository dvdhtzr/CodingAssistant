# Security requirements

## M0 execution warning

Host inventory and Ollama tool-call probes do not modify source or execute generated code. The OpenCode benchmark **does** execute an autonomous coding harness, builds and tests and may run untrusted generated programs. A temporary directory, environment scrubbing and OpenCode permissions **are not** an OS sandbox.

`tools/m0/benchmark.py` refuses execution unless `--acknowledge-unisolated-m0-risk` is supplied. That flag does **not** provide isolation. Run only in a throwaway sandboxed machine/container/VM with:
- no personal files or unrelated repositories mounted;
- no production secrets or credentials;
- no admin/root capabilities;
- no unapproved outbound network;
- no access to the running CodingAssistant control plane.

## M1 mandatory invariants

- Separate control plane (authenticated API/approvals/secrets) and execution plane (OpenCode/build/test/worktree) from inference (Ollama).
- Enforce file, process and network restrictions across all child processes.
- Prevent reading credentials, escaping workspace, writing other repositories, altering security controls or OS config, escalating privileges and unauthorized Git remotes.
- Planning must be read-only, coding requires approved exact plan/hash, execution history must be durable and reconciled after failures.
- Security tests must attempt escapes via symlinks/path traversal, subprocesses, networking, and Git remotes, not just ask the model to obey.
- If any required negative test fails, **disable autonomous execution** on that platform.

OpenClaw's built-in sandbox does **not automatically wrap external ACP coding harnesses**. OpenCode's native permission configuration is defense in depth only.

## Network and Git

Start locally on 127.0.0.1. Do not expose Ollama/Gateway/OpenCode/Mission Control directly to the public internet; LAN access requires authenticated encrypted transport. No cloud AI.

The owner authorized GitHub development pushes, PRs and merges in the current ChatGPT conversation. The actual CodingAssistant product must still enforce its plan- and operation-scoped owner approval flow for its future autonomous agents. Never store secrets or private inventory in Git.



## M1.8 — owner Dell ACP initialize verification (GREEN, November 19, 2026)

The Dell ran `python3 tools/m1/acpx_initialize_probe.py` against the
pinned combined image ID
`023bf9411fe23045dbdea9c777ed8618dec6553a81417c9aec2d15f7cf5b99ea`
and received **`M1.8 ACPX_OPENCODE_INITIALIZE_PASS`**.
The *real* `acpx@0.21.0` runtime and OpenCode 2.3.0 completed ACP
`initialize`, negotiated `protocol_version: 1`, admitted/spawned
**exactly one** approved `opencode acp` child, shut down the runtime
and removed the guest. No `session/new`, `session/prompt`, model,
network or project file operation occurred. Evidence:
`artifacts/m1/acpx-initialize-33e62303215e.json`.
This is a protocol handshake test, **not Gateway-owned delegation**.

## M1.9 — OS-enforced adversarial boundary qualification (next)

Before enabling an actual Gateway-owned coding session, confirm that
our exact combined image's rootless OS boundary rejects hostile
operations, **including operations launched by child processes**.
The new `tools/m1/os_negative_boundary_probe.py` runs twelve
independent negative cases inside ONE temporary rootless/no-network,
no-mount/no-published-port, no-capability, non-root, read-only-root
Podman guest. CPU/memory/PID budgets and one disposable `/tmp`
tmpfs apply. Tested attempted violations:

1. Writing an OS config file in `/etc`.
2. Symlink-based write escape into `/etc/passwd`.
3. `..` traversal write into `/etc`.
4. Reading `/etc/shadow`.
5. Reading shadow credentials through `/proc/1/root`.
6. Accessing the owner's unmounted CodingAssistant host worktree path.
7. Inheriting sensitive provider/GitHub/Ollama credentials.
8. Changing the executing process to root.
9. Opening an outbound TCP connection to documentation-only
   `203.0.113.1:443` despite `--network=none`.
10. Reaching the *host* Ollama via the container's
    `127.0.0.1:11434` (must not be reachable).
11. Bypassing network restrictions from an independently spawned
    child Node process.
12. Modifying `/etc` from an independently spawned child Node process.

Passing observations MUST show each case was denied or its protected
target was unavailable. One exposure, malformed observation,
unexpected guest exit, wrong image, or failed container cleanup
returns **BLOCKED**, and autonomous execution remains disabled.
Only bounded boolean observations are saved in append-only ignored
`artifacts/m1/os-negative-audit-*.json`, not raw error logs.
No Gateway, ACP session, OpenCode coding task, model, inference
or host repository is executed/modified. This probe does not add
a writable project mount, network route, or permission override.

**Run on Dell after the corresponding PR is GREEN and merged:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/os_negative_boundary_probe.py
```

Expected success: `M1.9 NEGATIVE_SECURITY_PASS` and **12/12**
individually denied. The tests are a **bounded, image-specific
security layer**; they do NOT prove that future projects,
symlink policies for approved worktrees, Git remote permissions,
Gateway-to-ACP session/plan approval workflows, Ollama socket
boundaries, or Windows-host controls are safe. All still require
targeted tests and explicit authorization before autonomous coding.
Overall M1 remains **YELLOW**.
