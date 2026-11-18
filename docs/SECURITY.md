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
