# M1 secure orchestration — status

**Status (November 19, 2026): YELLOW / M1.0 read-only host discovery prepared.**

## Accepted M0 baseline

Owner-run frozen benchmark: real local `qwen3.5:4b`, native OpenCode
`write`, verified independent rootless Podman compile/test in **all 9/9**
fresh task/repetition attempts. Model-free unit and container test harnesses
passed prior to deployment.

Independent owner Dell audit: `M0.18 EVIDENCE_REVIEW_PASS`, all nine
`EVIDENCE_PASS`, and `Recorded evidence audited: QUALIFIED`.
Preserved local results and append-only independent-audit evidence paths:

- `artifacts/m0/evaluation-20261119T161744Z-83706eec/results.json`
- `artifacts/m0/evaluation-20261119T161744Z-83706eec/independent-safety-audit-2b967f74b002.json`

Limitations remain those documented in
`docs/M0_INDEPENDENT_EVIDENCE_AUDIT.md`; this is not blanket security
certification, independent Windows-host acceptance, or permission to
launch an unrestricted Gateway or coding harness.

## Current M1.0 slice

Version-only `tools/m1/discovery.py` inventory on each host (Dell first).
It detects installed Node, OpenClaw, Podman and informational host OpenCode
versions without installing, changing config, connecting to inference,
starting a Gateway, or invoking ACP. Missing binaries or incompatible Node
produce `PREREQUISITES_BLOCKED`, not a security bypass.

Current official OpenClaw runtime: Node 24.16.0+ or Node 26.1.0+, with
24.22.1+/26.12.1+ strongly preferred for security. Previously observed
Dell Node 22.22.1 is unsupported. The embedded ACPX runtime is an
`@openclaw/acpx` plugin, not a required separate `acpx` CLI.
We will not upgrade the Linux host, install OpenClaw, or start a Gateway
without first reviewing the discovery output and choosing the smallest
safe, pinned installation boundary. A dedicated rootless Gateway image
may be preferable to modifying working M0 tooling.

**Next owner command:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/discovery.py
```

Expected output: `M1.0 PREREQUISITES_BLOCKED` or
`M1.0 DISCOVERY_READY`, followed by component version tokens and
`Gateway/ACP/agent execution: NOT_ATTEMPTED`.

## M1 exit gates still pending

Real OpenClaw Gateway → embedded acpx ACP runtime → pinned OpenCode/Ollama
compatibility; read-only planning; canonical exact plan-version/hash
approval; worktree-only coding; ten OS-enforced child-process negative tests;
durable event state and crash/reconnect/cancel reconciliation; independent
Linux and Windows host acceptance. A failing negative test disables
autonomous execution on that host. No M1 completion claims before all gates.

See `docs/IMPLEMENTATION_PLAN.md`, `docs/INTEGRATION.md`,
`docs/SECURITY.md`, and `docs/M1_GATEWAY_DISCOVERY.md`.

**M1.0 owner Dell discovery (2026-11-19):** `PREREQUISITES_BLOCKED` as
expected: host Node `v22.22.1` unsupported; host OpenClaw absent;
host OpenCode `v2.3.2` informational; Podman `5.7.0` available.
No Gateway, ACP or agent execution occurred. M0 remains GREEN.

**M1.1 next:** Prefer separately pinned official image
`ghcr.io/openclaw/openclaw:2026.11.5` in rootless Podman rather than
upgrading installed host Node/OpenClaw. Owner explicitly executes
`python3 tools/m1/gateway_image_probe.py --prepare` to download the
one official image, then validate its immutable identity and patched
Node/OpenClaw versions with fixed no-network, no-mount version probes.
No Gateway or ACP is started and no host packages are installed.
This is an isolated runtime **preflight**, not M1 qualification.
See `docs/M1_GATEWAY_DISCOVERY.md`. M1 remains YELLOW.

**M1.1 Dell qualification (Nov 19): RUNTIME_IMAGE_PASS.** The owner downloaded the official pinned `ghcr.io/openclaw/openclaw:2026.11.5` and verified immutable local image ID `405dcc682726f127d316d435e29d3c5d0cdecb502aef6d74384ce6dd4ffe2700` and registry digest `ghcr.io/openclaw/openclaw@sha256:97781490d6cd01cec8d1f0ad300ff8bedfaae18ec5b8c30666aaebc470a6309f`. The sealed image includes Node `v24.22.1` and OpenClaw `2026.11.5`; host Node/OpenClaw unchanged. No Gateway or ACP ran. Evidence local `artifacts/m1/gateway-image-probe.json`. **M1.1 image/version preflight GREEN; M1 overall YELLOW.**

**M1.2 next:** One *temporary real Gateway* startup on the same immutable ID in rootless, network-disabled, non-root, cap-free, read-only Podman, with NO published ports or host mounts. Create an ephemeral random token; `--allow-unconfigured --bind loopback`. Query only the guest-local `/healthz`, `/startupz`, and `/readyz`, then always stop/remove the container and verify cleanup. No ACP session, model call, project access or stored Gateway profile. Operator: `python3 tools/m1/gateway_loopback_probe.py`. This is an isolated Gateway smoke, **not** ACP integration or M1 security qualification.
