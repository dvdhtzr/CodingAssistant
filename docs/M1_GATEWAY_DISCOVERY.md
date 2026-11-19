# M1.0 — Read-only Gateway/ACP integration discovery

**Current milestone status:** M0 **GREEN on the Dell at documented evidence assurance**;
M1 **YELLOW / NOT YET SECURITY-QUALIFIED**. Windows host qualification and
M1 end-to-end operational security are separate gates.

The original approved M1 architecture remains:
ASP.NET Core control plane → OpenClaw Gateway → ACP runtime → OpenCode
→ locally pinned Ollama. M0's rootless, no-network OpenCode runner and independent
evaluator stay reusable. Do **not** assume that OpenClaw's own tool restrictions
automatically sandbox a child ACP coding harness.

## First operational step: inventory, not installation

Run this on the Dell:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/discovery.py
```

The reviewed Python script invokes only installed binaries with `--version`:
Node, OpenClaw, OpenCode (host presence informational), and Podman. It
does **not** install software, launch the Gateway or ACP, load OpenClaw
configuration, read environment secrets, contact Ollama, run model code,
or change existing projects. Output contains version tokens and booleans,
not executable paths, provider keys or arbitrary command logs. Evidence is
stored in ignored `artifacts/m1/gateway-discovery.json`.

It may report `M1.0 PREREQUISITES_BLOCKED` if the OpenClaw executable
is absent or host Node is too old. This is a **useful discovery outcome**,
not a failure of the already-qualified M0 or permission to install an
unreviewed CLI. No M1 autonomous coding is enabled by `DISCOVERY_READY`.

### Runtime compatibility verified in current official docs (Nov 19, 2026)

OpenClaw supports Node 24.16.0+ on the 24.x line or Node 26.1.0+
on the 26.x line. **Node 24.22.1+ or 26.12.1+ is strongly recommended
for security**; Node 22 is unsupported. Node 27 development releases
are intentionally not eligible for this initial production Gateway
integration (a more restrictive project choice). The Dell previously
reported Node 22.22.1, so it is **expected** to report an incompatible
host runtime until independently remedied. We do **not** automatically
upgrade the host: a separately pinned rootless Gateway container with
a supported patched Node is a possible M1 implementation path that
avoids disrupting the existing M0 toolchain.

OpenClaw's packaged `@openclaw/acpx` plugin **embeds** the ACP runtime;
there is no requirement to install a standalone `acpx` binary.
The plugin and its Gateway state are **NOT_INSPECTED** by this first
version-only probe. Its harness target id for OpenCode is `opencode`.
Do not silently select Claude/Codex/cloud providers or configure a cloud
fallback. ACP sessions run without interactive terminal permission prompts;
noninteractive permissions and a distinct external OS sandbox must be
verified before any coding turn.

Official source references checked for M1 kickoff:
- https://docs.openclaw.ai/install/node
- https://docs.openclaw.ai/tools/acp-agents/quickstart
- https://docs.openclaw.ai/tools/acp-agents-setup
- https://docs.openclaw.ai/tools/acp-agents

## M1 staged acceptance (no owner design questionnaire)

1. **M1.0:** Version-only discovery and patched Node/OpenClaw packaging
   decision, with model/cloud calls disabled.
2. **M1.1:** Read-only Gateway/embedded ACP plugin/OpenCode harness
   discovery under an explicit control-plane vs execution-plane boundary.
   Verify the actual runtime protocol/version; use **no invented Gateway
   methods** and no writes to original repos.
3. **M1.2:** Read-only coding-plan request with canonical plan version/hash;
   owner approval must exactly match both before execution is permitted.
4. **M1.3:** Single approved worktree-scoped coding turn, independently
   OS-enforced child-process, file and network confinement, exact provider
   pin and explicit denial of external Git remotes.
5. **M1.4:** Ten adversarial security negative tests, durable run events,
   reconnect, cancellation and crash recovery/reconciliation. Every
   security failure disables autonomous execution on that OS.
6. **M1 exit:** Linux *and* Windows need separately validated runtime and
   security gates before cross-platform M1 GREEN. A Dell-only pass cannot
   be used to claim Windows is qualified.

Keep deferred pluggable skills design in the backlog; don't let untrusted
skill prompts or scripts bypass owner approval, exact file scope, or OS
enforcement. No .NET API server, SQLite database, Mission Control UI,
or distributed runner is introduced in M1.0.

## Owner M1.0 outcome — November 19, 2026

The Dell reported `M1.0 PREREQUISITES_BLOCKED`: host Node `v22.22.1`
(unsupported), OpenClaw absent, host OpenCode `v2.3.2`, Podman `5.7.0`.
This is an expected M1 host-compatibility blocker, **not an M0 regression**.
The M0 qualified image retains its own pinned OpenCode/runtime.

### M1.1 — official versioned Gateway image probe (no Gateway startup)

To leave host Node and OpenClaw untouched, this probe uses the official
`ghcr.io/openclaw/openclaw:2026.11.5` image, never moving `latest`.
The official Podman setup requires a host OpenClaw CLI; our initial
**version-only** probe deliberately checks the image without that CLI.

Run on the Dell, after this change is merged:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/gateway_image_probe.py --prepare
```

Explicit `--prepare` pulls only this single official versioned image into
the user-owned rootless Podman store. Host Node/OpenClaw packages are NOT
installed or modified. The probe then verifies an immutable image ID and
official GHCR repository digest, and executes only `node --version` and
`node /app/openclaw.mjs --version` with network disabled, no host mounts,
read-only root filesystem, non-root identity, no capabilities and no-new-privileges.
It requires recommended security-patched Node and exact OpenClaw 2026.11.5.
No Gateway/ACP session/model inference is launched. Model state, secrets,
original projects, and worktrees are not mounted or inspected.

After the image is available, an offline rerun needs no pull:

```bash
python3 tools/m1/gateway_image_probe.py
```

Results go into Git-ignored `artifacts/m1/gateway-image-probe.json`.
A `RUNTIME_IMAGE_PASS` only certifies the image's version/startup discovery;
it is NOT a working OpenClaw Gateway or ACP session. If blocked, stop;
do not widen networking or replace host Node merely to get a green probe.

Next M1 stages must start and verify an isolated Gateway using explicitly
reviewed local-only configuration, then validate its embedded ACPX plugin
and the external OpenCode harness. Independent execution-plane isolation,
exact owner plan/hash approval, negative tests and Windows acceptance
remain mandatory.

Current primary references:
- https://docs.openclaw.ai/install/podman
- https://docs.openclaw.ai/install/node
- https://docs.openclaw.ai/tools/acp-agents-setup
- https://github.com/openclaw/openclaw/pkgs/container/openclaw

## M1.1 owner Dell result and M1.2 actual Gateway loopback smoke

**M1.1 RUNTIME_IMAGE_PASS**. On Nov 19 the owner verified the official image with immutable ID `405dcc682726f127d316d435e29d3c5d0cdecb502aef6d74384ce6dd4ffe2700` and official registry digest `ghcr.io/openclaw/openclaw@sha256:97781490d6cd01cec8d1f0ad300ff8bedfaae18ec5b8c30666aaebc470a6309f`. The image runs patched Node `v24.22.1` and OpenClaw `2026.11.5`. No host installation or Gateway process occurred.

**Next owner command after PR green and merge:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/gateway_loopback_probe.py
```

Unlike the previous version-only check, this launches a **real temporary OpenClaw Gateway**. It requires exactly the image's already-verified immutable ID/digest and starts it with `gateway --allow-unconfigured --bind loopback --port 18789` inside rootless Podman. The guest has **network=none** (its own loopback remains available), no host-project or config mounts, no host ports, zero capabilities, no new privileges, and read-only root except a disposable `/tmp` tmpfs. A fresh random Gateway token is provided by a private ephemeral environment file, never printed or stored in audit evidence. `HOME` and Gateway state are entirely ephemeral inside the guest. No ACP agent, coding task, remote Git operation, or Ollama inference is configured or requested.

The fixed monitor invokes Node within the isolated guest to request only three documented HTTP probes: `/healthz` (process liveness), `/startupz` (startup completion), and `/readyz` (readiness). **All three must return HTTP 200**. The monitor also requires the Gateway process to remain alive. The container is removed after the check, even on failure; a failed cleanup prevents PASS. Only status codes and safe flags are recorded in ignored `artifacts/m1/gateway-loopback-smoke.json`.

This is a **Gateway runtime test only**, not a working ACP/ACPX connection. If readiness remains 503, keep M1.2 BLOCKED, inspect its explicit local evidence and actual failure cause before adjusting config. Never relax networking or mount your home directory to force a green result. The later true ACP bridge must still have external OS-enforced harness isolation, strict owner approval, explicit local Ollama model pinning and negative security tests.

Command contract sources: https://docs.openclaw.ai/cli/gateway/running and https://docs.openclaw.ai/gateway/health .


## M1.2 owner result — first real isolated Gateway process: GREEN

On November 19, the Dell returned `M1.2 GATEWAY_LOOPBACK_PASS` with
rootless execution, `--network=none`, no host mounts, no published ports,
and independently observed HTTP 200 for `/healthz`, `/startupz`, `/readyz`.
The temporary Gateway was verified removed afterward. No ACP, model,
cloud service or coding agent was involved. Evidence:
`artifacts/m1/gateway-loopback-smoke.json`. Overall M1 remains YELLOW.

## M1.3 — inspect ACPX packaging in the exact pinned Gateway image

OpenClaw upstream tag `v2026.11.5` has plugin metadata at
`extensions/acpx/openclaw.plugin.json` and package
`@openclaw/acpx` version `2026.11.5`, depending on `acpx` version
`0.21.0`. Its Dockerfile uses `OPENCLAW_EXTENSIONS` to select optional
plugins; the root distribution excludes `dist/extensions/acpx/**`.
Consequently a healthy Gateway alone proves nothing about packaged ACPX.

**Next Dell command, after this PR is merged:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/acpx_image_inventory.py
```

This **reads only five fixed package/manifest paths inside the existing
rootless immutable Gateway image**, by executing trusted built-in Node
filesystem reads in a `podman run --network=none --read-only` container.
No config, host folders, model, API, adapter, or Gateway is accessed.
No pull, package install or `npx`/`npm` execution occurs.

Result is `M1.3 INVENTORY_COMPLETE` plus a separate distribution finding:
`NOT_BUNDLED`, `FILES_PRESENT_NOT_ACTIVATED`, or
`INCOMPLETE_OR_UNEXPECTED`. The first result is anticipated but must be
established on the actual Dell image. An inventory result **cannot**
authorize ACP sessions or coding agents. If the image lacks ACPX, plan
a version-pinned, reviewed derived image or plugin install in a separate
unprivileged build context, rather than enabling network access inside
the Gateway/agent runtime. The ACPX OpenCode adapter itself must be
verified independently, including its spawn command and strict local
model pin and process isolation.

Official version-tagged implementation evidence:
- `https://github.com/openclaw/openclaw/blob/v2026.11.5/Dockerfile`
- `https://github.com/openclaw/openclaw/blob/v2026.11.5/extensions/acpx/package.json`
- `https://github.com/openclaw/openclaw/blob/v2026.11.5/extensions/acpx/openclaw.plugin.json`
- `https://docs.openclaw.ai/tools/acp-agents-setup`
